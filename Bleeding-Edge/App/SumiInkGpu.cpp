#include <d3d11.h>
#include <d3d9.h>
#include <wrl/client.h>
#include <cstring>
#include <cmath>
#include <chrono>
#include <cwchar>
#include <initializer_list>
#include <new>
#include <utility>
#include "SumiInkShader.h"
#include "WaterLightVertex.h"
#include "WaterLightPixel.h"
#include "PresentVertex.h"
#include "PresentPixel.h"
#include "ParticleVertex.h"
#include "ParticlePixel.h"

using Microsoft::WRL::ComPtr;

namespace {
// WPF composes Direct3D 9Ex surfaces. One device serves every view and lives for the process.
IDirect3D9Ex* nineFactory = nullptr;
IDirect3DDevice9Ex* nineDevice = nullptr;

HRESULT NineDevice(bool renew, IDirect3DDevice9Ex** result)
{
    if (renew && nineDevice) {
        nineDevice->Release();
        nineDevice = nullptr;
    }
    if (!nineDevice) {
        if (!nineFactory) {
            HRESULT status = Direct3DCreate9Ex(D3D_SDK_VERSION, &nineFactory);
            if (FAILED(status)) return status;
        }
        D3DPRESENT_PARAMETERS present{};
        present.Windowed = TRUE;
        present.SwapEffect = D3DSWAPEFFECT_DISCARD;
        present.hDeviceWindow = GetDesktopWindow();
        present.BackBufferWidth = present.BackBufferHeight = 1;
        present.BackBufferFormat = D3DFMT_UNKNOWN;
        present.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
        HRESULT status = nineFactory->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, present.hDeviceWindow,
            D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE,
            &present, nullptr, &nineDevice);
        if (FAILED(status)) return status;
    }
    *result = nineDevice;
    return S_OK;
}

struct Surface {
    UINT width = 0, height = 0;
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> input;
    ComPtr<ID3D11UnorderedAccessView> output;
};

enum Pass : UINT {
    TransportVelocity, CurlAndDivergence, Confine, Pressure, Project,
    TransportInk, Push, Deposit, Composite, Drop, MoveOutline, RelaxPressure, WaterStep, WaterShow,
    WaterBlurX, WaterBlurY, FerroliquidShow, ReferenceWaterStep, Resample, Unused19,
    FlowConfine, FlowAdvectVelocity, FlowAdvectDye, FlowSplatDye, FlowSplatVelocity, FlowMaskDye, FlowMaskForce,
    FluidShow, Unused28, Unused29, ParticleStep, ParticleSeed, ParticlesShow, ParticleSpawn, ParticleGlowStep,
    ParticleEject, WordEject, SumPigment, BalancePigment, TraceMaterial, FilterDensity, EvolveCloud, SeedCloud, AbsorbFlow, AdvectFreshInk, FlowRecoil, FlowKick, SeedAmbient, SeedAmbientVelocity
};
static_assert(FlowConfine == 20 && ParticlesShow == 32 && WordEject == 36 && FlowKick == 46, "pass numbers match the shader");

// Kind 1 was the Fire view, removed on 2026-11-15 to be rebuilt later.
enum FlowKind : int { DyeFluid, Swarm = 2 };

struct Parameters {
    UINT width, height;
    Pass operation;
    float phase, x, y, radius, strength;
    float velocityScaleX, velocityScaleY, time, ink;
    float oldX, oldY, newX, newY;
};
static_assert(sizeof(Parameters) == 64, "HLSL constant buffer layout");

// The shared 2D incompressible core. Each view supplies its grid sizes and solver effort.
struct Fluid {
    Parameters parameters{};
    UINT iterations = 22;
    // How many flow cells from the letters' edges the word's push reaches, in Fluid, Fire and Particles.
    float reach = 3;
    bool water = false, ferroliquid = false, heldTransport = false;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> shader;
    ComPtr<ID3D11Buffer> constants, tuning, lineWakeBuffer, colorGradient, readingBlur;
    struct LineWake { float x, y, radius, birth, nx, ny, strength, padding; };
    LineWake lineWakes[64]{};
    unsigned nextWake = 0;
    bool wakesChanged = false;
    ComPtr<ID3D11SamplerState> sampler;
    Surface velocity, velocityWork, derivatives, pressure, pressureWork;
    Surface pigment, pigmentWork, mask, outline, frame, staging;
    // An 8:1 reduction needs at most five levels even at the D3D11 texture limit.
    Surface massLevels[5], heldMass;
    Surface cloudField, cloudWork, departure, density, freshInk, freshInkWork;
    bool cloudSeeded = false;
    bool showDetail = false;
    unsigned massLevelCount = 0, stepsSinceBalance = 0;
    bool massCaptured = false;
    Surface waterMask, waterBlur, previousWaterMask;
    Surface letterPivots;
    Surface caustics;
    ComPtr<ID3D11RenderTargetView> causticTarget;
    ComPtr<ID3D11VertexShader> lightVertex;
    ComPtr<ID3D11PixelShader> lightPixel;
    ComPtr<ID3D11BlendState> lightBlend;
    ComPtr<ID3D11RasterizerState> lightRaster;
    ComPtr<ID3D11Texture2D> shared;
    ComPtr<ID3D11RenderTargetView> sharedTarget;
    ComPtr<IDirect3DTexture9> mirror;
    ComPtr<IDirect3DSurface9> mirrorSurface;
    ComPtr<ID3D11VertexShader> presentVertex;
    ComPtr<ID3D11PixelShader> presentPixel;
    ComPtr<ID3D11Query> disjoint, started, finished;
    // The Fluid, Fire and Particles views.
    int kind = DyeFluid;
    Surface particles, particlesWork, glow, particleGlow, particleGlowWork;
    ComPtr<ID3D11RenderTargetView> glowTarget;
    ComPtr<ID3D11VertexShader> particleVertex;
    ComPtr<ID3D11PixelShader> particlePixel;
    ComPtr<ID3D11BlendState> additive;
    bool timing = false, timingOpen = false;
    float milliseconds = 0;
    bool maskMoved = false;
    bool pending = false;
    struct Recoil {
        Surface mask;
        std::chrono::steady_clock::time_point born;
        float x=0,y=0,age=1,beforeX=.82f,beforeY=.62f;
    };
    Recoil flowRecoils[8];
    unsigned nextFlowRecoil=0,flowStroke=0;

    static float EntranceEase(float t,bool sine)
    {
        return sine ? (1-std::cos(3.14159265f*t))*.5f : 1-(1-t)*(1-t)*(1-t);
    }

    void AdvanceFlowRecoils()
    {
        for(auto& recoil:flowRecoils) {
            if(recoil.age>=.145f || !recoil.mask.input)continue;
            recoil.age=std::chrono::duration<float>(std::chrono::steady_clock::now()-recoil.born).count();
            float x,y,t;
            if(recoil.age<.048f) {
                t=EntranceEase(recoil.age/.048f,false);
                x=.82f+.28f*t;y=.62f+.32f*t;
            } else if(recoil.age<.086f) {
                t=EntranceEase((recoil.age-.048f)/.038f,true);
                x=1.10f-.14f*t;y=.94f+.14f*t;
            } else {
                t=(recoil.age-.086f)/.059f;
                if(t>1)t=1;
                t=EntranceEase(t,false);
                x=.96f+.04f*t;y=1.08f-.08f*t;
            }
            parameters.x=recoil.x*velocity.width;
            parameters.y=recoil.y*velocity.height;
            parameters.oldX=recoil.beforeX;parameters.oldY=recoil.beforeY;
            parameters.newX=x;parameters.newY=y;
            Dispatch(FlowRecoil,velocityWork,velocity,&recoil.mask);
            std::swap(velocity,velocityWork);
            recoil.beforeX=x;recoil.beforeY=y;
        }
    }

    ~Fluid()
    {
        if (pending && context && staging.texture && frame.texture) {
            // A key can submit work and the window can close before the next readback.
            context->CopyResource(staging.texture.Get(), frame.texture.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if (SUCCEEDED(context->Map(staging.texture.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
                context->Unmap(staging.texture.Get(), 0);
        }
        if (context) {
            context->ClearState();
            context->Flush();
        }
    }

    HRESULT Make(Surface& surface, DXGI_FORMAT format, UINT bindings, UINT width, UINT height,
                 bool readback = false)
    {
        surface = Surface{};
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = surface.width = width;
        desc.Height = surface.height = height;
        desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
        desc.Format = format;
        desc.BindFlags = bindings;
        desc.Usage = readback ? D3D11_USAGE_STAGING : D3D11_USAGE_DEFAULT;
        desc.CPUAccessFlags = readback ? D3D11_CPU_ACCESS_READ : 0;
        HRESULT status = device->CreateTexture2D(&desc, nullptr, surface.texture.GetAddressOf());
        if (SUCCEEDED(status) && (bindings & D3D11_BIND_SHADER_RESOURCE))
            status = device->CreateShaderResourceView(surface.texture.Get(), nullptr, surface.input.GetAddressOf());
        if (SUCCEEDED(status) && (bindings & D3D11_BIND_UNORDERED_ACCESS)) {
            status = device->CreateUnorderedAccessView(surface.texture.Get(), nullptr, surface.output.GetAddressOf());
            if (SUCCEEDED(status)) {
                const float clear[4]{};
                context->ClearUnorderedAccessViewFloat(surface.output.Get(), clear);
            }
        }
        return status;
    }

    HRESULT Initialize(bool forWater, bool forFerroliquid)
    {
        water = forWater;
        ferroliquid = forFerroliquid;
        const D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
        HRESULT status = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE,
            nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, &level, 1, D3D11_SDK_VERSION,
            device.GetAddressOf(), nullptr, context.GetAddressOf());
        if (FAILED(status)) return status;
        status = device->CreateComputeShader(SumiInkBytecode, sizeof(SumiInkBytecode), nullptr, shader.GetAddressOf());
        if (FAILED(status)) return status;
        status = device->CreateVertexShader(PresentVertex, sizeof(PresentVertex), nullptr, presentVertex.GetAddressOf());
        if (FAILED(status)) return status;
        status = device->CreatePixelShader(PresentPixel, sizeof(PresentPixel), nullptr, presentPixel.GetAddressOf());
        if (FAILED(status)) return status;
        D3D11_BUFFER_DESC buffer{};
        buffer.ByteWidth = sizeof(Parameters);
        buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        status = device->CreateBuffer(&buffer, nullptr, constants.GetAddressOf());
        if (FAILED(status)) return status;
        const float noGradient[12]{};
        buffer.ByteWidth = sizeof(noGradient);
        D3D11_SUBRESOURCE_DATA initialGradient{};
        initialGradient.pSysMem = noGradient;
        status = device->CreateBuffer(&buffer, &initialGradient, colorGradient.GetAddressOf());
        if (FAILED(status)) return status;
        context->CSSetConstantBuffers(3, 1, colorGradient.GetAddressOf());
        const float noBlur[4]{};
        buffer.ByteWidth = sizeof(noBlur);
        D3D11_SUBRESOURCE_DATA initialBlur{};
        initialBlur.pSysMem = noBlur;
        status = device->CreateBuffer(&buffer, &initialBlur, readingBlur.GetAddressOf());
        if (FAILED(status)) return status;
        context->CSSetConstantBuffers(4, 1, readingBlur.GetAddressOf());
        buffer.ByteWidth = sizeof(lineWakes);
        D3D11_SUBRESOURCE_DATA initialWakes{};
        initialWakes.pSysMem = lineWakes;
        status = device->CreateBuffer(&buffer, &initialWakes, lineWakeBuffer.GetAddressOf());
        if (FAILED(status)) return status;
        context->CSSetConstantBuffers(2, 1, lineWakeBuffer.GetAddressOf());
        // The 0.6.27 look, in the order of the shader's Tuning buffer.
        const float builtIn[128] = { 0.982f, 0.035f, 0.20f, 0.024f, 1, 0.88f, 0, 0,
                                    13, 0.982f, 0.30f, 1, 1, 11, 0.13f, 0.23f,
                                    1, 1, 1, 0, 0.094f, 0.122f, 0.184f, 0,
                                    0.095f, 0.445f, 0.55f, 0, 0.49f, 0.77f, 0.70f, 0,
                                    0.82f, 0.09f, 0, 0, 1, 0.55f, 0, 0, 1, 0.96f, 0.82f, 0,
                                    0.04f, 0.30f, 0.95f, 0, 0.15f, 0.90f, 1, 0,
                                    0.949f, 0.925f, 0.875f, 0,
                                    0.110f, 0.110f, 0.133f, 0, 0.110f, 0.294f, 0.490f, 0, 0.761f, 0.231f, 0.180f, 0,
                                    0.227f, 0.490f, 0.365f, 0, 0.788f, 0.580f, 0.102f, 0,
                                    0.70f, 1, 0.94f, 0,
                                    1, 1, 1, 0,
                                    0.61f, 0.88f, 0.83f, 0, 0.37f, 0.78f, 0.92f, 0,
                                    0.028f, 0.020f, 0.024f, 0,
                                    0.008f, 0.010f, 0.026f, 0,
                                    1, 1, 1, 1,
                                    0.11f, 0.63f, 0, 1,
                                    0, 13, 0, 0,
                                    0.57f, 0.80f, 0.74f, 0,
                                    0.030f, 0.042f, 0.085f, 0,
                                    1, 1, 1, 0.29f,
                                    35.3f, 16.4f, 0, 0 };
        buffer.ByteWidth = sizeof(builtIn);
        D3D11_SUBRESOURCE_DATA initial{};
        initial.pSysMem = builtIn;
        status = device->CreateBuffer(&buffer, &initial, tuning.GetAddressOf());
        if (FAILED(status)) return status;
        D3D11_SAMPLER_DESC filter{};
        filter.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        filter.AddressU = filter.AddressV = filter.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        filter.ComparisonFunc = D3D11_COMPARISON_NEVER;
        filter.MaxLOD = D3D11_FLOAT32_MAX;
        status = device->CreateSamplerState(&filter, sampler.GetAddressOf());
        if (FAILED(status)) return status;
        D3D11_RASTERIZER_DESC raster{};
        raster.FillMode = D3D11_FILL_SOLID;
        raster.CullMode = D3D11_CULL_NONE;
        raster.DepthClipEnable = TRUE;
        status = device->CreateRasterizerState(&raster, lightRaster.GetAddressOf());
        if (FAILED(status)) return status;

        // Timing is optional. A driver without timestamp queries still runs the views.
        D3D11_QUERY_DESC query{};
        query.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
        if (SUCCEEDED(device->CreateQuery(&query, disjoint.GetAddressOf()))) {
            query.Query = D3D11_QUERY_TIMESTAMP;
            if (FAILED(device->CreateQuery(&query, started.GetAddressOf())) ||
                FAILED(device->CreateQuery(&query, finished.GetAddressOf())))
                disjoint.Reset();
        }

        context->CSSetShader(shader.Get(), nullptr, 0);
        context->CSSetSamplers(0, 1, sampler.GetAddressOf());
        ID3D11Buffer* buffers[] = { constants.Get(), tuning.Get() };
        context->CSSetConstantBuffers(0, 2, buffers);
        return S_OK;
    }

    // Smoke grids follow the window. Existing dye and motion are resampled into the new grids.
    HRESULT AllocateInk(UINT width, UINT height, UINT flowWidth, UINT flowHeight, UINT solverIterations)
    {
        const UINT read = D3D11_BIND_SHADER_RESOURCE;
        const UINT write = D3D11_BIND_UNORDERED_ACCESS;
        const Surface oldVelocity = velocity, oldPigment = pigment;
        massLevelCount = 0;
        massCaptured = false;
        iterations = solverIterations;
        parameters.velocityScaleX = static_cast<float>(width) / flowWidth;
        parameters.velocityScaleY = static_cast<float>(height) / flowHeight;
        HRESULT status = S_OK;
        for (Surface* surface : { &velocity, &velocityWork, &derivatives }) {
            status = Make(*surface, DXGI_FORMAT_R16G16_FLOAT, read | write, flowWidth, flowHeight);
            if (FAILED(status)) return status;
        }
        for (Surface* surface : { &pressure, &pressureWork }) {
            status = Make(*surface, DXGI_FORMAT_R32_FLOAT, read | write, flowWidth, flowHeight);
            if (FAILED(status)) return status;
        }
        for (Surface* surface : { &pigment, &pigmentWork }) {
            status = Make(*surface, DXGI_FORMAT_R16G16B16A16_FLOAT, read | write, width, height);
            if (FAILED(status)) return status;
        }
        status = Make(mask, DXGI_FORMAT_R8G8B8A8_UNORM, read, width, height);
        if (FAILED(status)) return status;
        status = Make(outline, DXGI_FORMAT_R8G8B8A8_UNORM, read, flowWidth, flowHeight);
        if (FAILED(status)) return status;
        if(freshInk.texture && !PrepareFreshInk())return E_OUTOFMEMORY;
        status = AllocateOutput(width, height);
        if (FAILED(status)) return status;
        if (oldPigment.texture) {
            Dispatch(Resample, velocity, oldVelocity);
            Dispatch(Resample, pigment, oldPigment);
        }
        return S_OK;
    }

    HRESULT AllocateFlow(UINT width, UINT height, UINT flowWidth, UINT flowHeight, UINT solverIterations,
                         UINT particleWidth, UINT particleHeight)
    {
        HRESULT status = AllocateInk(width, height, flowWidth, flowHeight, solverIterations);
        if (FAILED(status) || kind != Swarm) return status;
        status = Make(glow, DXGI_FORMAT_R16G16B16A16_FLOAT,
                      D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, width, height);
        if (FAILED(status)) return status;
        glowTarget.Reset();
        status = device->CreateRenderTargetView(glow.texture.Get(), nullptr, glowTarget.GetAddressOf());
        if (FAILED(status)) return status;
        if (!particleVertex) {
            status = device->CreateVertexShader(ParticleVertex, sizeof(ParticleVertex), nullptr, particleVertex.GetAddressOf());
            if (FAILED(status)) return status;
            status = device->CreatePixelShader(ParticlePixel, sizeof(ParticlePixel), nullptr, particlePixel.GetAddressOf());
            if (FAILED(status)) return status;
            D3D11_BLEND_DESC blend{};
            auto& target = blend.RenderTarget[0];
            target.BlendEnable = TRUE;
            target.SrcBlend = target.DestBlend = D3D11_BLEND_ONE;
            target.BlendOp = D3D11_BLEND_OP_ADD;
            target.SrcBlendAlpha = target.DestBlendAlpha = D3D11_BLEND_ONE;
            target.BlendOpAlpha = D3D11_BLEND_OP_ADD;
            target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
            status = device->CreateBlendState(&blend, additive.GetAddressOf());
            if (FAILED(status)) return status;
        }
        if (particles.width != particleWidth || particles.height != particleHeight) {
            const UINT state = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
            for (Surface* surface : { &particles, &particlesWork }) {
                status = Make(*surface, DXGI_FORMAT_R32G32B32A32_FLOAT, state, particleWidth, particleHeight);
                if (FAILED(status)) return status;
            }
            for (Surface* surface : { &particleGlow, &particleGlowWork }) {
                status = Make(*surface, DXGI_FORMAT_R16G16B16A16_FLOAT, state, particleWidth, particleHeight);
                if (FAILED(status)) return status;
            }
            Dispatch(ParticleSeed, particles, particlesWork);
        }
        return S_OK;
    }

    void DrawParticles(float halfSize, float light, float speedColor, float glowBoost)
    {
        parameters.ink = glowBoost;
        parameters.width = glow.width;
        parameters.height = glow.height;
        parameters.radius = halfSize;
        parameters.strength = light;
        parameters.phase = speedColor;
        context->UpdateSubresource(constants.Get(), 0, nullptr, &parameters, 0, 0);
        const float clear[4]{};
        context->ClearRenderTargetView(glowTarget.Get(), clear);
        context->OMSetRenderTargets(1, glowTarget.GetAddressOf(), nullptr);
        context->OMSetBlendState(additive.Get(), nullptr, 0xffffffff);
        D3D11_VIEWPORT viewport{0, 0, static_cast<float>(glow.width), static_cast<float>(glow.height), 0, 1};
        context->RSSetViewports(1, &viewport);
        context->RSSetState(lightRaster.Get());
        context->IASetInputLayout(nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(particleVertex.Get(), nullptr, 0);
        context->PSSetShader(particlePixel.Get(), nullptr, 0);
        ID3D11Buffer* buffers[] = { constants.Get(), tuning.Get() };
        context->VSSetConstantBuffers(0, 2, buffers);
        context->PSSetConstantBuffers(0, 2, buffers);
        // Each particle reads its state and how brightly it still glows.
        ID3D11ShaderResourceView* inputs[] = { particles.input.Get(), particleGlow.input.Get() };
        context->VSSetShaderResources(0, 2, inputs);
        context->Draw(6 * particles.width * particles.height, 0);
        ID3D11ShaderResourceView* none[2]{};
        context->VSSetShaderResources(0, 2, none);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
        pending = true;
    }

    HRESULT AllocateWater(UINT width, UINT height)
    {
        const UINT read = D3D11_BIND_SHADER_RESOURCE;
        const UINT write = D3D11_BIND_UNORDERED_ACCESS;
        const UINT flowWidth = ferroliquid ? 768 : 384, flowHeight = ferroliquid ? 432 : 216;
        parameters.velocityScaleX = static_cast<float>(width) / flowWidth;
        parameters.velocityScaleY = static_cast<float>(height) / flowHeight;
        HRESULT status = S_OK;
        if (!velocity.texture) {
            for (Surface* surface : { &velocity, &velocityWork }) {
                status = Make(*surface, DXGI_FORMAT_R16G16_FLOAT, read | write, flowWidth, flowHeight);
                if (FAILED(status)) return status;
            }
            status = Make(letterPivots, DXGI_FORMAT_R32G32_FLOAT, read, flowWidth, 1);
            if (FAILED(status)) return status;
            status = Make(waterMask, DXGI_FORMAT_R8G8B8A8_UNORM, ferroliquid ? read : read | write, flowWidth, flowHeight);
            if (FAILED(status)) return status;
            status = Make(waterBlur, DXGI_FORMAT_R16_FLOAT, read | write, flowWidth, flowHeight);
            if (FAILED(status)) return status;
            if (!ferroliquid) {
                status = Make(previousWaterMask, DXGI_FORMAT_R8G8B8A8_UNORM, read | write, flowWidth, flowHeight);
                if (FAILED(status)) return status;
                status = InitializeCaustics();
                if (FAILED(status)) return status;
            }
            status = Make(outline, DXGI_FORMAT_R16_FLOAT, read | write, flowWidth, flowHeight);
            if (FAILED(status)) return status;
        }
        return AllocateOutput(width, height);
    }

    // The frame is composed by compute, then drawn once into a surface shared with WPF.
    HRESULT AllocateOutput(UINT width, UINT height)
    {
        HRESULT status = Make(frame, DXGI_FORMAT_R8G8B8A8_UNORM,
                              D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, width, height);
        if (FAILED(status)) return status;
        status = Make(staging, DXGI_FORMAT_R8G8B8A8_UNORM, 0, width, height, true);
        if (FAILED(status)) return status;
        mirrorSurface.Reset();
        mirror.Reset();
        sharedTarget.Reset();
        shared.Reset();
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
        status = device->CreateTexture2D(&desc, nullptr, shared.GetAddressOf());
        if (FAILED(status)) return status;
        status = device->CreateRenderTargetView(shared.Get(), nullptr, sharedTarget.GetAddressOf());
        if (FAILED(status)) return status;
        ComPtr<IDXGIResource> resource;
        status = shared.As(&resource);
        if (FAILED(status)) return status;
        HANDLE handle = nullptr;
        status = resource->GetSharedHandle(&handle);
        if (FAILED(status)) return status;
        for (int attempt = 0; attempt < 2; ++attempt) {
            // A second attempt replaces a Direct3D 9 device that was lost with the display.
            IDirect3DDevice9Ex* nine = nullptr;
            status = NineDevice(attempt == 1, &nine);
            if (FAILED(status)) continue;
            HANDLE opened = handle;
            status = nine->CreateTexture(width, height, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8,
                                         D3DPOOL_DEFAULT, mirror.ReleaseAndGetAddressOf(), &opened);
            if (SUCCEEDED(status)) break;
        }
        if (FAILED(status)) return status;
        return mirror->GetSurfaceLevel(0, mirrorSurface.GetAddressOf());
    }

    void BeginFrame()
    {
        if (!disjoint) return;
        if (timing && !timingOpen) {
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT ticks{};
            UINT64 first = 0, last = 0;
            const UINT quiet = D3D11_ASYNC_GETDATA_DONOTFLUSH;
            if (context->GetData(disjoint.Get(), &ticks, sizeof(ticks), quiet) == S_OK &&
                context->GetData(started.Get(), &first, sizeof(first), quiet) == S_OK &&
                context->GetData(finished.Get(), &last, sizeof(last), quiet) == S_OK) {
                if (!ticks.Disjoint && ticks.Frequency && last > first)
                    milliseconds = static_cast<float>(static_cast<double>(last - first) * 1000.0 / ticks.Frequency);
                timing = false;
            }
        }
        if (!timing) {
            context->Begin(disjoint.Get());
            context->End(started.Get());
            timing = timingOpen = true;
        }
    }

    HRESULT Present()
    {
        context->OMSetRenderTargets(1, sharedTarget.GetAddressOf(), nullptr);
        context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
        D3D11_VIEWPORT viewport{0, 0, static_cast<float>(frame.width), static_cast<float>(frame.height), 0, 1};
        context->RSSetViewports(1, &viewport);
        context->RSSetState(lightRaster.Get());
        context->IASetInputLayout(nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(presentVertex.Get(), nullptr, 0);
        context->PSSetShader(presentPixel.Get(), nullptr, 0);
        context->PSSetSamplers(0, 1, sampler.GetAddressOf());
        context->PSSetShaderResources(0, 1, frame.input.GetAddressOf());
        context->Draw(3, 0);
        ID3D11ShaderResourceView* none = nullptr;
        context->PSSetShaderResources(0, 1, &none);
        context->OMSetRenderTargets(0, nullptr, nullptr);
        if (timingOpen) {
            context->End(finished.Get());
            context->End(disjoint.Get());
            timingOpen = false;
        }
        // WPF reads the shared surface from another device, so submit the work now.
        context->Flush();
        return device->GetDeviceRemovedReason();
    }

    HRESULT InitializeCaustics()
    {
        HRESULT status = Make(caustics, DXGI_FORMAT_R16_FLOAT,
                              D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, 1280, 720);
        if (FAILED(status)) return status;
        status = device->CreateRenderTargetView(caustics.texture.Get(), nullptr, causticTarget.GetAddressOf());
        if (FAILED(status)) return status;
        status = device->CreateVertexShader(WaterLightVertex, sizeof(WaterLightVertex), nullptr, lightVertex.GetAddressOf());
        if (FAILED(status)) return status;
        status = device->CreatePixelShader(WaterLightPixel, sizeof(WaterLightPixel), nullptr, lightPixel.GetAddressOf());
        if (FAILED(status)) return status;
        D3D11_BLEND_DESC blend{};
        auto& target = blend.RenderTarget[0];
        target.BlendEnable = TRUE;
        target.SrcBlend = target.DestBlend = D3D11_BLEND_ONE;
        target.BlendOp = D3D11_BLEND_OP_ADD;
        target.SrcBlendAlpha = target.DestBlendAlpha = D3D11_BLEND_ONE;
        target.BlendOpAlpha = D3D11_BLEND_OP_ADD;
        target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_RED;
        return device->CreateBlendState(&blend, lightBlend.GetAddressOf());
    }

    void ProjectCaustics()
    {
        parameters.width = 320;
        parameters.height = 180;
        context->UpdateSubresource(constants.Get(), 0, nullptr, &parameters, 0, 0);
        const float clear[4]{};
        context->ClearRenderTargetView(causticTarget.Get(), clear);
        context->OMSetRenderTargets(1, causticTarget.GetAddressOf(), nullptr);
        context->OMSetBlendState(lightBlend.Get(), nullptr, 0xffffffff);
        D3D11_VIEWPORT viewport{0, 0, static_cast<float>(caustics.width), static_cast<float>(caustics.height), 0, 1};
        context->RSSetViewports(1, &viewport);
        context->RSSetState(lightRaster.Get());
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(lightVertex.Get(), nullptr, 0);
        context->PSSetShader(lightPixel.Get(), nullptr, 0);
        ID3D11Buffer* buffers[] = { constants.Get(), tuning.Get() };
        context->VSSetConstantBuffers(0, 2, buffers);
        context->VSSetSamplers(0, 1, sampler.GetAddressOf());
        ID3D11ShaderResourceView* inputs[] = { velocity.input.Get(), nullptr, outline.input.Get(), letterPivots.input.Get() };
        context->VSSetShaderResources(0, 4, inputs);
        context->Draw(6 * parameters.width * parameters.height, 0);
        ID3D11ShaderResourceView* empty[4]{};
        context->VSSetShaderResources(0, 4, empty);
        context->OMSetRenderTargets(0, nullptr, nullptr);
    }

    void Dispatch(Pass pass, Surface& target, const Surface& source,
                  const Surface* field = nullptr, const Surface* extra = nullptr)
    {
        parameters.operation = pass;
        parameters.width = target.width;
        parameters.height = target.height;
        context->UpdateSubresource(constants.Get(), 0, nullptr, &parameters, 0, 0);
        ID3D11ShaderResourceView* inputs[] = { source.input.Get(), field ? field->input.Get() : nullptr,
                                             extra ? extra->input.Get() : nullptr, letterPivots.input.Get(),
                                             pass == Composite ? cloudField.input.Get() : nullptr,
                                             pass == Composite ? density.input.Get() : nullptr,
                                             pass == Composite ? freshInk.input.Get() : nullptr };
        context->CSSetShaderResources(0, 7, inputs);
        context->CSSetUnorderedAccessViews(0, 1, target.output.GetAddressOf(), nullptr);
        context->Dispatch((parameters.width + 7) / 8, (parameters.height + 7) / 8, 1);
        pending = true;
        ID3D11ShaderResourceView* noInputs[7]{};
        ID3D11UnorderedAccessView* noOutput = nullptr;
        context->CSSetUnorderedAccessViews(0, 1, &noOutput, nullptr);
        context->CSSetShaderResources(0, 7, noInputs);
    }

    void Disturb(const void* glyphs, float x, float y, float radius, float phase, float strength, bool letter, int ink)
    {
        parameters.x = x * velocity.width;
        parameters.y = y * velocity.height;
        parameters.radius = radius;
        parameters.phase = phase;
        parameters.strength = strength;
        parameters.ink = static_cast<float>(ink);
        Dispatch(Push, velocityWork, velocity);
        std::swap(velocity, velocityWork);
        if (glyphs) {
            context->UpdateSubresource(mask.texture.Get(), 0, nullptr, glyphs, mask.width * 4, 0);
            Dispatch(Deposit, pigmentWork, pigment, &mask);
            std::swap(pigment, pigmentWork);
        }
        else if (letter) {
            parameters.x = x * pigment.width;
            parameters.y = y * pigment.height;
            parameters.radius = radius * parameters.velocityScaleX;
            Dispatch(Drop, pigmentWork, pigment);
            std::swap(pigment, pigmentWork);
        }
    }

    bool PrepareFreshInk()
    {
        if(freshInk.width==pigment.width && freshInk.height==pigment.height && freshInkWork.output)return true;
        const Surface old=freshInk;
        const UINT flags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;
        if(FAILED(Make(freshInk,DXGI_FORMAT_R16G16B16A16_FLOAT,flags,pigment.width,pigment.height)) ||
           FAILED(Make(freshInkWork,DXGI_FORMAT_R16G16B16A16_FLOAT,flags,pigment.width,pigment.height)))return false;
        if(old.texture)Dispatch(Resample,freshInk,old);
        return true;
    }

    void AdvanceFreshInk()
    {
        if(!freshInk.texture || !PrepareFreshInk())return;
        Dispatch(AdvectFreshInk,freshInkWork,freshInk,&velocity,heldTransport?&departure:nullptr);
        std::swap(freshInk,freshInkWork);
    }

    bool PrepareHeldField()
    {
        if (departure.width == pigment.width && departure.height == pigment.height && departure.output) return true;
        const UINT flags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        return SUCCEEDED(Make(departure,DXGI_FORMAT_R32G32_FLOAT,flags,pigment.width,pigment.height));
    }

    void UpdateMaterial()
    {
        if (!showDetail) return;
        UINT w = (pigment.width + 15) / 16, h = (pigment.height + 15) / 16;
        UINT rows = pigment.height * 2 < 1440 ? pigment.height * 2 : 1440;
        UINT columns = (pigment.width * rows + pigment.height - 1) / pigment.height;
        if (cloudField.width != columns || cloudField.height != rows || !cloudWork.output || !density.output ||
            density.width != w || density.height != h) {
            const Surface previousCloud = cloudField;
            const bool hadCloud = cloudSeeded;
            const UINT flags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
            if (FAILED(Make(cloudField,DXGI_FORMAT_R16G16_FLOAT,flags,columns,rows)) ||
                FAILED(Make(cloudWork,DXGI_FORMAT_R16G16_FLOAT,flags,columns,rows)) ||
                FAILED(Make(density,DXGI_FORMAT_R16G16B16A16_FLOAT,flags,w,h))) return;
            cloudSeeded = false;
            if (hadCloud && previousCloud.texture) {
                Dispatch(Resample,cloudField,previousCloud);
                cloudSeeded = true;
            }
        }
        if (!cloudSeeded) {
            Dispatch(SeedCloud,cloudField,cloudWork);
            cloudSeeded = true;
        } else if (heldTransport && departure.output) {
            Dispatch(EvolveCloud,cloudWork,cloudField,nullptr,&departure);
            std::swap(cloudField,cloudWork);
        }
        Dispatch(FilterDensity,density,pigment);
    }

    bool MeasurePigment(const Surface& dye)
    {
        if (massLevelCount == 0) {
            UINT w = dye.width, h = dye.height;
            const UINT bind = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
            unsigned count = 0;
            do {
                w = (w + 7) / 8;
                h = (h + 7) / 8;
                if (FAILED(Make(massLevels[count], DXGI_FORMAT_R32G32B32A32_FLOAT, bind, w, h))) return false;
                ++count;
            } while (w > 1 || h > 1);
            if (FAILED(Make(heldMass, DXGI_FORMAT_R32G32B32A32_FLOAT, bind, 1, 1))) return false;
            massLevelCount = count;
        }
        const Surface* input = &dye;
        for (unsigned i = 0; i < massLevelCount; ++i) {
            Dispatch(SumPigment, massLevels[i], *input);
            input = &massLevels[i];
        }
        return true;
    }

    void Advance()
    {
        parameters.time += 1.0f / 30;
        AdvanceFlowRecoils();
        if (wakesChanged) {
            context->UpdateSubresource(lineWakeBuffer.Get(), 0, nullptr, lineWakes, 0, 0);
            wakesChanged = false;
        }
        Dispatch(CurlAndDivergence, derivatives, velocity);
        Dispatch(Confine, velocityWork, velocity, &derivatives);
        Dispatch(CurlAndDivergence, derivatives, velocityWork);
        Dispatch(RelaxPressure, pressureWork, pressure);
        std::swap(pressure, pressureWork);
        for (UINT i = 0; i < iterations; ++i) {
            Dispatch(Pressure, pressureWork, pressure, &derivatives);
            std::swap(pressure, pressureWork);
        }
        Dispatch(Project, velocity, velocityWork, &pressure);
        Dispatch(TransportVelocity, velocityWork, velocity);
        std::swap(velocity, velocityWork);
        // velocityWork holds the pressure-projected field from this step. Use it
        // to sample the local rotation for each eddy in the held field.
        if (heldTransport && !massCaptured && MeasurePigment(pigment)) {
            context->CopyResource(heldMass.texture.Get(), massLevels[massLevelCount - 1].texture.Get());
            massCaptured = true;
            stepsSinceBalance = 0;
        }
        if (heldTransport && PrepareHeldField())
            Dispatch(TraceMaterial,departure,pigment,&velocityWork);
        Dispatch(TransportInk,pigmentWork,pigment,heldTransport ? &velocityWork : &velocity,
                 heldTransport ? &departure : nullptr);
        if (heldTransport && massCaptured && ++stepsSinceBalance >= 12 && MeasurePigment(pigmentWork)) {
            // Filtering and half-float storage slowly bias the sum over a long read.
            // Balance existing pigment in-place over time, entirely on the GPU.
            Dispatch(BalancePigment, pigment, pigmentWork, &massLevels[massLevelCount - 1], &heldMass);
            stepsSinceBalance = 0;
            return;
        }
        std::swap(pigment, pigmentWork);
    }
};
}

extern "C" __declspec(dllexport) HRESULT SumiCreate(UINT width, UINT height, UINT flowWidth, UINT flowHeight,
                                                   UINT iterations, Fluid** result)
{
    *result = nullptr;
    Fluid* fluid = new (std::nothrow) Fluid;
    if (!fluid) return E_OUTOFMEMORY;
    HRESULT status = fluid->Initialize(false, false);
    if (SUCCEEDED(status)) status = fluid->AllocateInk(width, height, flowWidth, flowHeight, iterations);
    if (FAILED(status)) delete fluid;
    else *result = fluid;
    return status;
}

extern "C" __declspec(dllexport) HRESULT SumiResize(Fluid* fluid, UINT width, UINT height, UINT flowWidth,
                                                   UINT flowHeight, UINT iterations)
{
    HRESULT status = fluid->AllocateInk(width, height, flowWidth, flowHeight, iterations);
    return FAILED(status) ? status : fluid->device->GetDeviceRemovedReason();
}

extern "C" __declspec(dllexport) void* GpuSurface(Fluid* fluid)
{
    return fluid->mirrorSurface.Get();
}

extern "C" __declspec(dllexport) void GpuTune(Fluid* fluid, const float* values)
{
    fluid->showDetail = values[23] > 0;
    fluid->heldTransport = values[127] > 0.5f;
    if (!fluid->heldTransport) fluid->massCaptured = false;
    fluid->context->UpdateSubresource(fluid->tuning.Get(), 0, nullptr, values, 0, 0);
}

// Solver detail and push reach chosen in the tuning panel. Zero iterations keeps the quality tier's count.
extern "C" __declspec(dllexport) void GpuSetMotion(Fluid* fluid, UINT iterations, float reach)
{
    if (iterations > 0)
        fluid->iterations = iterations < 400 ? iterations : 400;
    fluid->reach = reach;
}

extern "C" __declspec(dllexport) float GpuMilliseconds(Fluid* fluid)
{
    return fluid->milliseconds;
}

extern "C" __declspec(dllexport) HRESULT GpuAdapter(Fluid* fluid, wchar_t* name, int capacity)
{
    ComPtr<IDXGIDevice> dxgi;
    HRESULT status = fluid->device.As(&dxgi);
    if (FAILED(status)) return status;
    ComPtr<IDXGIAdapter> adapter;
    status = dxgi->GetAdapter(adapter.GetAddressOf());
    if (FAILED(status)) return status;
    DXGI_ADAPTER_DESC description{};
    status = adapter->GetDesc(&description);
    if (FAILED(status)) return status;
    wcsncpy_s(name, capacity, description.Description, _TRUNCATE);
    return S_OK;
}

extern "C" __declspec(dllexport) void SumiDisturb(Fluid* fluid, const void* glyphs, float x, float y,
                                                 float radius, float phase, float strength, int letter, int ink)
{
    fluid->Disturb(glyphs, x, y, radius, phase, strength, letter != 0, ink);
}

extern "C" __declspec(dllexport) void SumiStirLine(Fluid* fluid, float x, float y, float radius,
                                                    float angle, float strength)
{
    auto& wake = fluid->lineWakes[fluid->nextWake++ % 64];
    wake = {x, y, radius / fluid->velocity.height, fluid->parameters.time,
            -std::sin(angle), std::cos(angle), strength, 0};
    fluid->wakesChanged = true;
}

extern "C" __declspec(dllexport) void SumiAbsorbFlow(Fluid* fluid,const void* glyphs,
    float x,float y,float velocity,float release,float push,float size,float gain)
{
    fluid->context->UpdateSubresource(fluid->mask.texture.Get(),0,nullptr,glyphs,fluid->mask.width*4,0);
    fluid->parameters.strength=gain;
    fluid->parameters.phase=fluid->parameters.time;
    fluid->Dispatch(Deposit,fluid->pigmentWork,fluid->pigment,&fluid->mask);
    std::swap(fluid->pigment,fluid->pigmentWork);
    if(fluid->PrepareFreshInk()) {
        fluid->Dispatch(AbsorbFlow,fluid->freshInkWork,fluid->freshInk,&fluid->mask);
        std::swap(fluid->freshInk,fluid->freshInkWork);
    }
    fluid->massCaptured=false;

    // Match Typing's deposit and caret impulses. Weight each batch by its newly
    // released opacity so a slow fade does not repeatedly deliver a whole keystroke.
    auto& p=fluid->parameters;
    p.x=x*fluid->velocity.width;p.y=y*fluid->velocity.height;
    p.phase=(fluid->flowStroke++*2.399963f);
    p.radius=28*size;p.strength=5.4f*push;p.newX=release;
    p.oldX=velocity*fluid->velocity.width/30;p.oldY=0;
    fluid->Dispatch(FlowKick,fluid->velocityWork,fluid->velocity,&fluid->mask);
    std::swap(fluid->velocity,fluid->velocityWork);
    p.phase+=2.399963f;p.radius=34*size;p.strength=8.4f*push;
    fluid->Dispatch(FlowKick,fluid->velocityWork,fluid->velocity,&fluid->mask);
    std::swap(fluid->velocity,fluid->velocityWork);

    auto& recoil=fluid->flowRecoils[fluid->nextFlowRecoil++%8];
    if(recoil.mask.width!=fluid->velocity.width || recoil.mask.height!=fluid->velocity.height) {
        const UINT flags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;
        if(FAILED(fluid->Make(recoil.mask,DXGI_FORMAT_R16G16B16A16_FLOAT,flags,
            fluid->velocity.width,fluid->velocity.height)))return;
    }
    fluid->Dispatch(Resample,recoil.mask,fluid->mask);
    recoil.x=x;recoil.y=y;recoil.age=0;recoil.beforeX=.82f;recoil.beforeY=.62f;
    recoil.born=std::chrono::steady_clock::now();
}


// Explicit one-time ambient seed; never called by standalone typing views.
extern "C" __declspec(dllexport) void SumiReadingBlur(Fluid* fluid, const float* values)
{
    fluid->context->UpdateSubresource(fluid->readingBlur.Get(), 0, nullptr, values, 0, 0);
}

extern "C" __declspec(dllexport) void SumiColorGradient(Fluid* fluid, const float* values)
{
    fluid->context->UpdateSubresource(fluid->colorGradient.Get(), 0, nullptr, values, 0, 0);
}

extern "C" __declspec(dllexport) void SumiSeedAmbient(Fluid* fluid)
{
    fluid->Dispatch(SeedAmbient, fluid->pigment, fluid->pigmentWork);
    fluid->Dispatch(SeedAmbientVelocity, fluid->velocity, fluid->velocityWork);
}

extern "C" __declspec(dllexport) HRESULT SumiFrame(Fluid* fluid)
{
    fluid->BeginFrame();
    fluid->Advance();
    fluid->AdvanceFreshInk();
    fluid->UpdateMaterial();
    fluid->Dispatch(Composite,fluid->frame,fluid->pigment,nullptr,
                    fluid->massLevelCount ? &fluid->massLevels[fluid->massLevelCount-1] : nullptr);
    return fluid->Present();
}

// Selects how pigment is stored and shown. 100 or more is the Ink view's absorbing ink.
extern "C" __declspec(dllexport) void SumiSetInk(Fluid* fluid, float ink)
{
    fluid->parameters.ink = ink;
}

extern "C" __declspec(dllexport) void SumiSetOutline(Fluid* fluid, const void* glyphs)
{
    fluid->context->UpdateSubresource(fluid->outline.texture.Get(), 0, nullptr, glyphs,
                                     fluid->outline.width * 4, 0);
}

extern "C" __declspec(dllexport) void SumiMoveOutline(Fluid* fluid, float oldX, float oldY, float newX, float newY)
{
    fluid->parameters.oldX = oldX;
    fluid->parameters.oldY = oldY;
    fluid->parameters.newX = newX;
    fluid->parameters.newY = newY;
    fluid->Dispatch(MoveOutline, fluid->velocityWork, fluid->velocity, &fluid->outline);
    std::swap(fluid->velocity, fluid->velocityWork);
}

extern "C" __declspec(dllexport) void SumiDestroy(Fluid* fluid)
{
    delete fluid;
}

extern "C" __declspec(dllexport) HRESULT WaterCreate(UINT width, UINT height, int ferroliquid, Fluid** result)
{
    *result = nullptr;
    Fluid* fluid = new (std::nothrow) Fluid;
    if (!fluid) return E_OUTOFMEMORY;
    HRESULT status = fluid->Initialize(true, ferroliquid != 0);
    if (SUCCEEDED(status)) status = fluid->AllocateWater(width, height);
    if (FAILED(status)) delete fluid;
    else *result = fluid;
    return status;
}

extern "C" __declspec(dllexport) HRESULT WaterResize(Fluid* fluid, UINT width, UINT height)
{
    HRESULT status = fluid->AllocateWater(width, height);
    return FAILED(status) ? status : fluid->device->GetDeviceRemovedReason();
}

extern "C" __declspec(dllexport) void WaterSetOutline(Fluid* fluid, const void* glyphs, int ferroliquid)
{
    if (!ferroliquid && !fluid->maskMoved) {
        std::swap(fluid->waterMask, fluid->previousWaterMask);
        fluid->maskMoved = true;
    }
    fluid->context->UpdateSubresource(fluid->waterMask.texture.Get(), 0, nullptr, glyphs,
                                     fluid->waterMask.width * 4, 0);
    if (ferroliquid || fluid->maskMoved) {
        fluid->parameters.radius = ferroliquid ? 12.0f : 5.0f;
        fluid->Dispatch(WaterBlurX, fluid->waterBlur, fluid->waterMask);
        fluid->Dispatch(WaterBlurY, fluid->outline, fluid->waterBlur);
    }
}

extern "C" __declspec(dllexport) void WaterSetPivots(Fluid* fluid, const float* pivots)
{
    fluid->context->UpdateSubresource(fluid->letterPivots.texture.Get(), 0, nullptr, pivots,
                                      fluid->letterPivots.width * 2 * sizeof(float), 0);
}

extern "C" __declspec(dllexport) HRESULT WaterFrame(Fluid* fluid, float pulse, int held, int ferroliquid,
                                                   float dt, float dropX, float dropY, float drop)
{
    fluid->BeginFrame();
    if (!ferroliquid) {
        auto& params = fluid->parameters;
        params.oldX = dt / 3;
        params.ink = static_cast<float>(held);
        params.phase = fluid->maskMoved ? 0.012f : 0;
        params.strength = pulse;
        params.x = dropX;
        params.y = dropY;
        params.radius = drop;
        for (int i = 0; i < 3; ++i) {
            params.time += params.oldX;
            fluid->Dispatch(ReferenceWaterStep, fluid->velocityWork, fluid->velocity,
                            &fluid->waterMask, &fluid->previousWaterMask);
            std::swap(fluid->velocity, fluid->velocityWork);
            params.phase = params.strength = params.radius = 0;
        }
        fluid->maskMoved = false;
        fluid->ProjectCaustics();
        fluid->Dispatch(WaterShow, fluid->frame, fluid->velocity, &fluid->caustics, &fluid->outline);
        return fluid->Present();
    }
    fluid->parameters.ink = static_cast<float>(held);
    fluid->parameters.strength = pulse;
    constexpr int waveSteps = 5;
    for (int i = 0; i < waveSteps; ++i) {
        fluid->parameters.oldX = dt / waveSteps;
        fluid->parameters.time += fluid->parameters.oldX;
        fluid->Dispatch(WaterStep, fluid->velocityWork, fluid->velocity, &fluid->outline);
        std::swap(fluid->velocity, fluid->velocityWork);
        fluid->parameters.strength = 0;
    }
    fluid->Dispatch(FerroliquidShow, fluid->frame, fluid->velocity, nullptr, &fluid->outline);
    return fluid->Present();
}

extern "C" __declspec(dllexport) HRESULT FlowCreate(int kind, UINT width, UINT height, UINT flowWidth, UINT flowHeight,
                                                   UINT iterations, UINT particleWidth, UINT particleHeight,
                                                   Fluid** result)
{
    *result = nullptr;
    Fluid* fluid = new (std::nothrow) Fluid;
    if (!fluid) return E_OUTOFMEMORY;
    fluid->kind = kind;
    HRESULT status = fluid->Initialize(false, false);
    if (SUCCEEDED(status))
        status = fluid->AllocateFlow(width, height, flowWidth, flowHeight, iterations, particleWidth, particleHeight);
    if (FAILED(status)) delete fluid;
    else *result = fluid;
    return status;
}

extern "C" __declspec(dllexport) HRESULT FlowResize(Fluid* fluid, UINT width, UINT height, UINT flowWidth,
                                                   UINT flowHeight, UINT iterations, UINT particleWidth,
                                                   UINT particleHeight)
{
    HRESULT status = fluid->AllocateFlow(width, height, flowWidth, flowHeight, iterations, particleWidth, particleHeight);
    return FAILED(status) ? status : fluid->device->GetDeviceRemovedReason();
}

// The current word at dye and flow resolution. Fire burns it and Space releases it.
extern "C" __declspec(dllexport) void FlowSetWord(Fluid* fluid, const void* mask, const void* outline)
{
    fluid->context->UpdateSubresource(fluid->mask.texture.Get(), 0, nullptr, mask, fluid->mask.width * 4, 0);
    fluid->context->UpdateSubresource(fluid->outline.texture.Get(), 0, nullptr, outline, fluid->outline.width * 4, 0);
}

extern "C" __declspec(dllexport) void FlowSplat(Fluid* fluid, float x, float y, float pushX, float pushY, float radius,
                                               float red, float green, float blue, float amount)
{
    auto& p = fluid->parameters;
    p.x = x * fluid->velocity.width;
    p.y = y * fluid->velocity.height;
    p.radius = radius;
    p.oldX = pushX;
    p.oldY = pushY;
    fluid->Dispatch(FlowSplatVelocity, fluid->velocityWork, fluid->velocity);
    std::swap(fluid->velocity, fluid->velocityWork);
    p.x = x * fluid->pigment.width;
    p.y = y * fluid->pigment.height;
    p.radius = radius * p.velocityScaleX;
    p.oldX = red;
    p.oldY = green;
    p.newX = blue;
    p.strength = amount;
    fluid->Dispatch(FlowSplatDye, fluid->pigmentWork, fluid->pigment);
    std::swap(fluid->pigment, fluid->pigmentWork);
}

extern "C" __declspec(dllexport) void FlowBurst(Fluid* fluid, float force, float lift, float red, float green,
                                               float blue, float amount)
{
    // A positive lift lays the word as the brightest layer instead of adding to what is there.
    auto& p = fluid->parameters;
    p.phase = lift;
    p.strength = amount;
    p.oldX = red;
    p.oldY = green;
    p.newX = blue;
    fluid->Dispatch(FlowMaskDye, fluid->pigmentWork, fluid->pigment, &fluid->mask);
    std::swap(fluid->pigment, fluid->pigmentWork);
    if (force == 0) return;
    p.strength = force;
    p.phase = 0;
    p.radius = fluid->reach;
    fluid->Dispatch(FlowMaskForce, fluid->velocityWork, fluid->velocity, &fluid->outline);
    std::swap(fluid->velocity, fluid->velocityWork);
}

extern "C" __declspec(dllexport) HRESULT FlowFrame(Fluid* fluid, const float* setting, int fueled)
{
    enum { Curl, VelocityKeep, DyeKeep, Unused3, Unused4, Unused5, Unused6, Exposure, Glow,
           Drag, ParticleSize, ParticleLight, SpeedColor, DyeTint, GlowFade, GlowBoost, FlowSpeed, SpeedLimit, GridPull };
    auto& p = fluid->parameters;
    fluid->BeginFrame();
    p.time += 1.0f / 60;
    (void)fueled;
    fluid->Dispatch(CurlAndDivergence, fluid->derivatives, fluid->velocity);
    p.strength = setting[Curl];
    p.radius = setting[SpeedLimit];
    fluid->Dispatch(FlowConfine, fluid->velocityWork, fluid->velocity, &fluid->derivatives);
    fluid->Dispatch(CurlAndDivergence, fluid->derivatives, fluid->velocityWork);
    fluid->Dispatch(RelaxPressure, fluid->pressureWork, fluid->pressure);
    std::swap(fluid->pressure, fluid->pressureWork);
    for (UINT i = 0; i < fluid->iterations; ++i) {
        fluid->Dispatch(Pressure, fluid->pressureWork, fluid->pressure, &fluid->derivatives);
        std::swap(fluid->pressure, fluid->pressureWork);
    }
    fluid->Dispatch(Project, fluid->velocity, fluid->velocityWork, &fluid->pressure);
    p.strength = setting[VelocityKeep];
    // No view rises. Fire moves like any other fluid and is fire only through its colors.
    p.phase = 0;
    p.oldX = setting[FlowSpeed];
    fluid->Dispatch(FlowAdvectVelocity, fluid->velocityWork, fluid->velocity, &fluid->pigment);
    std::swap(fluid->velocity, fluid->velocityWork);
    p.strength = setting[DyeKeep];
    // Flow speed also sets how far dye travels each step.
    const float scaleX = p.velocityScaleX, scaleY = p.velocityScaleY;
    p.velocityScaleX *= setting[FlowSpeed];
    p.velocityScaleY *= setting[FlowSpeed];
    fluid->Dispatch(FlowAdvectDye, fluid->pigmentWork, fluid->pigment, &fluid->velocity);
    std::swap(fluid->pigment, fluid->pigmentWork);
    p.velocityScaleX = scaleX;
    p.velocityScaleY = scaleY;

    if (fluid->kind == Swarm) {
        p.strength = setting[Drag];
        p.ink = setting[GridPull];
        p.oldX = setting[FlowSpeed] / fluid->velocity.width;
        p.oldY = setting[FlowSpeed] / fluid->velocity.height;
        fluid->Dispatch(ParticleStep, fluid->particlesWork, fluid->particles, &fluid->velocity);
        std::swap(fluid->particles, fluid->particlesWork);
        p.x = p.y = 0;
        p.strength = setting[GlowFade];
        fluid->Dispatch(ParticleGlowStep, fluid->particleGlowWork, fluid->particleGlow);
        std::swap(fluid->particleGlow, fluid->particleGlowWork);
        fluid->DrawParticles(setting[ParticleSize], setting[ParticleLight], setting[SpeedColor], setting[GlowBoost]);
        p.phase = setting[Exposure];
        p.strength = setting[DyeTint];
        fluid->Dispatch(ParticlesShow, fluid->frame, fluid->glow, &fluid->pigment);
    }
    else {
        p.phase = setting[Exposure];
        p.strength = setting[Glow];
        fluid->Dispatch(FluidShow, fluid->frame, fluid->pigment);
    }
    return fluid->Present();
}

// Pushes the fluid outward from the edges of a shape, such as the letter just typed.
// The word's own shape is kept for Space.
extern "C" __declspec(dllexport) void FlowPushShape(Fluid* fluid, const void* shape, const void* word, float strength)
{
    auto& p = fluid->parameters;
    auto* context = fluid->context.Get();
    context->UpdateSubresource(fluid->outline.texture.Get(), 0, nullptr, shape, fluid->outline.width * 4, 0);
    p.strength = strength;
    p.phase = 0;
    p.radius = fluid->reach;
    fluid->Dispatch(FlowMaskForce, fluid->velocityWork, fluid->velocity, &fluid->outline);
    std::swap(fluid->velocity, fluid->velocityWork);
    context->UpdateSubresource(fluid->outline.texture.Get(), 0, nullptr, word, fluid->outline.width * 4, 0);
}

// Moves a random share of the whole swarm onto the word's outlines and makes those particles glow.
// thickness is the outline band in mask pixels. The word's bounds run from (left, top) to (right, bottom), 0 to 1.
extern "C" __declspec(dllexport) void FlowSpawn(Fluid* fluid, float thickness, float share, float left, float top,
                                               float right, float bottom, float seed)
{
    if (fluid->kind != Swarm) return;
    auto& p = fluid->parameters;
    p.x = thickness;
    p.y = share;
    p.oldX = left;
    p.oldY = top;
    p.newX = right;
    p.newY = bottom;
    p.ink = seed;
    fluid->Dispatch(ParticleSpawn, fluid->particlesWork, fluid->particles, &fluid->mask);
    std::swap(fluid->particles, fluid->particlesWork);
    p.strength = 1;
    fluid->Dispatch(ParticleGlowStep, fluid->particleGlowWork, fluid->particleGlow);
    std::swap(fluid->particleGlow, fluid->particleGlowWork);
}

// The whole word bursts outward at once. Particles inside the letters are thrown toward the nearest edge,
// at particleStrength in window heights per step, and the fluid inside every letter is pushed the same way,
// at fluidStrength in flow cells per step and no faster than limit. The fluid push carries the burst even when
// the particles follow the flow exactly.
extern "C" __declspec(dllexport) void FlowEject(Fluid* fluid, float particleStrength, float fluidStrength, float limit)
{
    auto& p = fluid->parameters;
    if (fluid->kind == Swarm) {
        p.strength = particleStrength;
        fluid->Dispatch(ParticleEject, fluid->particlesWork, fluid->particles, &fluid->mask);
        std::swap(fluid->particles, fluid->particlesWork);
    }
    p.strength = fluidStrength;
    p.radius = limit;
    fluid->Dispatch(WordEject, fluid->velocityWork, fluid->velocity, &fluid->outline);
    std::swap(fluid->velocity, fluid->velocityWork);
}
