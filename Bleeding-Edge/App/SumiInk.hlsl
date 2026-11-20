// All quantities are in simulation cells per step, with Y increasing down the panel.
Texture2D<float4> source : register(t0);
Texture2D<float4> field : register(t1);
Texture2D<float4> relief : register(t2);
Texture2D<float2> letterPivots : register(t3);
Texture2D<float2> simulatedCloud : register(t4);
Texture2D<float4> cloudDensity : register(t5);
Texture2D<float4> freshFlowInk : register(t6);
RWTexture2D<float4> result : register(u0);
SamplerState linearClamp : register(s0);

cbuffer Parameters : register(b0)
{
    int2 size;
    uint operation;
    float phase;
    float2 origin;
    float radius;
    float strength;
    float2 velocityScale;
    float time;
    float ink;
    float2 previousScale;
    float2 entranceScale;
};

struct LineWake { float4 pose; float4 motion; };
cbuffer RibbonMotion : register(b2) { LineWake lineWakes[64]; };
cbuffer ColorGradient : register(b3)
{
    float4 colorStart, colorEnd, colorGradientControl;
};

// Optional presentation-only blur beneath the reading line.
cbuffer ReadingBlur : register(b4) { float4 readingBlurControl; };

// Values the tuning panel adjusts while a view runs. The defaults reproduce 0.6.27.
cbuffer Tuning : register(b1)
{
    float smokeKeep, smokeDrift, smokeCurl, smokeSpread;
    float smokeFade, smokeGlow, smokeSpeed, smokeLimit;
    float waterGain, waterKeep, waterSpeed, waterFlutter;
    float waterSwell, waterBend, waterCaustics, waterSpecks;
    // Colors chosen in the tuning panel. Only rgb is used.
    float4 smokeInk, smokePaper;
    float4 waterDeep, waterShallow;
    float4 fireEmber, fireFlame, fireCore;
    float4 particleSlow, particleFast;
    // The Ink view: paper color and five ink colors.
    float4 inkPaper;
    float4 inkColors[5];
    // The color of newly made particles in the Particles view, chosen for each keystroke.
    float4 particleGlow;
    // Smoke's ink color for the current deposit, chosen for each letter or word.
    float4 smokeCurrent;
    float4 waterCaustic, waterHighlight;
    float4 fireNight;
    float4 particleBackground;
    // Smoke and Ink: how hard a word's arrival kicks the fluid, how much ink a word lays, and how much a letter's wake leaves.
    // Water: the size of a raindrop.
    float smokeEntrance, smokeAmount, smokeWake, waterDropSize;
    // Smoke: how far toward the ink color its lightest and strongest shades go.
    // Water: speck glow and fineness, speck twinkle, and the direction from deep to shallow water in degrees.
    float smokeShadeLow, smokeShadeHigh, specksGlow, specksFine;
    float specksTwinkle, waterAngle, smokeGrain, smokeTurbulence;
    float4 specksColor;
    float4 fluidBackground;
    // Water caustics: where light starts to show, how sharply it rises, how soft its lines are, the depth to the
    // floor, and the sun's direction (degrees around and degrees from straight down).
    float causticThreshold, causticSharp, causticSoft, waterDepth;
    float lightAround, lightTilt, cloudLighting, heldTransport;
};

// The direction sunlight travels into the water.
float3 Sunlight()
{
    float around = radians(lightAround), tilt = radians(lightTilt);
    return float3(sin(tilt) * cos(around), sin(tilt) * sin(around), -cos(tilt));
}

static const int2 X = int2(1, 0);
static const int2 Y = int2(0, 1);

float4 Read(Texture2D<float4> image, int2 p)
{
    return image.Load(int3(clamp(p, int2(0, 0), size - 1), 0));
}

float4 Sample(Texture2D<float4> image, float2 p)
{
    return image.SampleLevel(linearClamp, (p + 0.5) / size, 0);
}

float2 PigmentVelocity(int2 p)
{
    return Sample(field, p).xy * velocityScale;
}

float2 WallVelocity(float2 velocity, int2 p)
{
    if (p.x == 0 || p.x == size.x - 1) velocity.x = 0;
    if (p.y == 0 || p.y == size.y - 1) velocity.y = 0;
    return velocity;
}

float Outline(float2 cell,float2 scale,float2 pivot)
{
    float2 uv=((cell+.5-pivot)/scale+pivot)/size;
    return field.SampleLevel(linearClamp,uv,0).a;
}

// Shared by the Typing entrance and dissolving Flow glyphs.
float2 OutlineImpulse(float2 p,float2 previous,float2 current,float2 pivot)
{
    float before=Outline(p,previous,pivot);
    float now=Outline(p,current,pivot);
    float2 gradient=float2(Outline(p+float2(4,0),current,pivot)-Outline(p-float2(4,0),current,pivot),
                           Outline(p+float2(0,4),current,pivot)-Outline(p-float2(0,4),current,pivot));
    float2 motion=clamp((p+.5-pivot)*(1-previous/current),-8,8);
    float edge=saturate(length(gradient)*1.3+abs(now-before));
    return motion*(.1*now+.8*edge)*smokeEntrance;
}

// The phase varies by glyph. A slightly detuned carrier avoids locking to the display cadence.
float2 VibratingWordUV(float2 uv, float seconds)
{
    uint width, height;
    letterPivots.GetDimensions(width, height);
    float2 center = letterPivots.Load(int3(min((uint)(saturate(uv.x) * width), width - 1), 0, 0));
    float seed = frac(dot(center, float2(47.13, 83.27)));
    float scale = 1 + 0.008 * waterFlutter * sin(6.2831853 * ((27.8 + 1.6 * seed) * seconds + seed));
    return center + (uv - center) / scale;
}

float HeldWordHeight(float2 uv)
{
    return 0.65 * relief.SampleLevel(linearClamp, VibratingWordUV(uv, time), 0).x;
}

float WaterSurfaceHeight(float2 uv)
{
    return source.SampleLevel(linearClamp, uv, 0).x + (ink > 0 ? HeldWordHeight(uv) : 0);
}

float2 WaterDriftSlope(float2 uv)
{
    float2 p = uv * float2(1.77778, 1);
    float2 a = float2(0.88, 0.47), b = float2(-0.45, 0.89);
    float across = dot(p, a), along = dot(p, b);
    float bend = 1.65 * sin(11 * along + 1.2 * sin(7 * across) + time * 0.82);
    float crossBend = 2.4 * sin(10 * across - 0.91 * time + sin(6 * along));
    return waterSwell * (a * (0.219 * cos(53 * across + bend - 2.8 * time)) +
                         b * (0.152 * cos(54 * along + crossBend + 2.3 * time)));
}

float CausticLight(float2 uv)
{
    const float2 texel = causticSoft / float2(1280, 720);
    const float2 x = float2(texel.x, 0), y = float2(0, texel.y);
    return 0.40 * field.SampleLevel(linearClamp, uv, 0).x +
           0.15 * (field.SampleLevel(linearClamp, uv + x, 0).x + field.SampleLevel(linearClamp, uv - x, 0).x +
                   field.SampleLevel(linearClamp, uv + y, 0).x + field.SampleLevel(linearClamp, uv - y, 0).x);
}

float WaterMask(Texture2D<float4> mask, float2 uv)
{
    return mask.SampleLevel(linearClamp, uv, 0).a;
}

float WaterMaskCross(Texture2D<float4> mask, float2 uv, float2 e)
{
    float2 x = float2(e.x, 0), y = float2(0, e.y);
    return 0.2 * (WaterMask(mask, uv) + WaterMask(mask, uv + x) + WaterMask(mask, uv - x) +
                  WaterMask(mask, uv + y) + WaterMask(mask, uv - y));
}

float WaterMaskRing(Texture2D<float4> mask, float2 uv, float2 e)
{
    float2 x = float2(e.x, 0), y = float2(0, e.y);
    float2 a = e * 0.7071, b = a * float2(1, -1);
    return 0.125 * (WaterMask(mask, uv + x) + WaterMask(mask, uv - x) +
                    WaterMask(mask, uv + y) + WaterMask(mask, uv - y) +
                    WaterMask(mask, uv + a) + WaterMask(mask, uv - a) +
                    WaterMask(mask, uv + b) + WaterMask(mask, uv - b));
}


float GrainHash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float Grain(float2 p)
{
    float2 cell = floor(p), f = frac(p);
    f = f * f * (3 - 2 * f);
    return lerp(lerp(GrainHash(cell), GrainHash(cell + float2(1, 0)), f.x),
                lerp(GrainHash(cell + float2(0, 1)), GrainHash(cell + 1), f.x), f.y);
}

// The curl of a smooth noise field folds the dye without sourcing or compressing it.
float2 NoiseCurl(float2 p)
{
    float2 cell = floor(p), f = frac(p);
    float2 blend = f * f * (3 - 2 * f);
    float2 slope = 6 * f * (1 - f);
    float a = GrainHash(cell), b = GrainHash(cell + float2(1, 0));
    float c = GrainHash(cell + float2(0, 1)), d = GrainHash(cell + 1);
    return float2(lerp(c - a, d - b, blend.x) * slope.y,
                  -lerp(b - a, d - c, blend.y) * slope.x);
}

// Sample scalar potentials in different orientations; rotate their curls back
// into panel coordinates so each contribution has zero analytical divergence.
float2 ObliqueCurl(float2 p, float2 axis, float2 offset)
{
    float2 side = float2(-axis.y, axis.x);
    float2 curl = NoiseCurl(float2(dot(p, axis), dot(p, side)) + offset);
    return axis * curl.x + side * curl.y;
}

// A radial change in angle has unit area scale at every radius. Composing
// these maps rolls a filament without sucking pigment into a low-pressure core.
// All distances here are panel heights, so circles stay round on wide screens.
float2 Twist(float2 pos, float2 pivot, float extent, float angle, float2 bounds)
{
    float2 wall = min(pivot, bounds - pivot);
    extent = min(extent, min(wall.x, wall.y));
    float2 arm = pos - pivot;
    float r2 = dot(arm,arm);
    if (extent <= .001 || r2 >= extent * extent) return pos;
    float t = 1 - r2 / (extent * extent);
    float sn, cs;
    sincos(angle * t * t, sn, cs);
    return pivot + float2(cs*arm.x - sn*arm.y, sn*arm.x + cs*arm.y);
}

float2 EddyLayer(float2 pos, float2 bounds, float pitch, float seed, float rate)
{
    float2 slide = float2(.46*sin(.81*time + seed), .39*cos(.68*time - seed))
                 + time * float2(.16*cos(seed), .17*sin(seed));
    float2 tile = floor(pos / pitch + slide + seed);
    float2 h = float2(GrainHash(tile + seed), GrainHash(tile.yx + seed + 41));
    float2 pivot = (tile + .5 + .18*(h-.5) - slide - seed)*pitch;
    float extent = pitch * (.35 + .10*h.y);
    // One shared angular speed per eddy. Sampling velocity at the moving
    // pigment position instead would compress it wherever the speed changes.
    float2 local = field.SampleLevel(linearClamp, saturate(pivot / bounds), 0).xy;
    float spin = (h.x > .5 ? 1 : -1) * (.70 + .30*h.y) + .18 * clamp((local.y-local.x)*.16,-1,1);
    return Twist(pos,pivot,extent,rate*spin,bounds);
}

float2 HeldDeparture(float2 pixel)
{
    float2 bounds = float2(size.x / (float)size.y, 1);
    float2 pos = (pixel + .5) / size.y;
    // Read back through the wakes, then through overlapping eddies at five
    // scales. No layer paints a texture; all visible detail belongs to the dye.
    [loop] for (int i=0; i<64; ++i)
    {
        LineWake w = lineWakes[i];
        float age = time - w.pose.w;
        if (w.pose.z <= 0 || age < 0 || age >= 1.35) continue;
        float r = w.pose.z;
        float2 pivot = w.pose.xy * bounds;
        if (any(abs(pos-pivot) > 3.3*r)) continue;
        float envelope = smoothstep(0,.12,age) * (1-smoothstep(.30,1.35,age));
        float turn = .0028 * w.motion.z * envelope;
        float2 normal = w.motion.xy;
        float2 along = float2(normal.y,-normal.x);
        // The broad pair folds the bank around the passing line. Smaller
        // rotations at its shoulders split those folds into connected tendrils.
        float2 offset = .78*r*normal;
        pos = Twist(pos,pivot+offset,1.85*r,-turn,bounds);
        pos = Twist(pos,pivot-offset,1.85*r, turn,bounds);
        pos = Twist(pos,pivot+.88*r*along+.82*r*normal,.62*r, turn*2.3,bounds);
        pos = Twist(pos,pivot-.88*r*along-.82*r*normal,.62*r,-turn*2.3,bounds);
        pos = Twist(pos,pivot+1.25*r*along+.96*r*normal,.29*r,-turn*3.6,bounds);
        pos = Twist(pos,pivot-1.25*r*along-.96*r*normal,.29*r, turn*3.6,bounds);
    }
    float pace = smokeTurbulence * clamp(sqrt(smokeSpeed / 3), .5, 1.4);
    if (pace > 0)
    {
        pos = Twist(pos,bounds*.5,min(bounds.x,bounds.y)*.49,.026*pace,bounds);
        pos = EddyLayer(pos,bounds,.086,23.3,.030*pace);
        pos = EddyLayer(pos,bounds,.193,17.9,.040*pace);
        pos = EddyLayer(pos,bounds,.367, 3.4,.060*pace);
        pos = EddyLayer(pos,bounds,.697,11.7,.055*pace);
        pos = EddyLayer(pos,bounds,.993, 5.2,.045*pace);
    }
    if(smokeInk.w>0)
    {
        uint width,height;
        field.GetDimensions(width,height);
        float2 start=pos,back=pos;
        [unroll] for(int step=0;step<3;++step) {
            float2 uv=saturate((start+back)*.5/bounds);
            back=start-field.SampleLevel(linearClamp,uv,0).xy*(smokeSpeed/max(1u,height));
        }
        pos=clamp(back,0,bounds);
    }
    return pos * size.y - .5;
}

float FractalPotential(float2 p, int octaves)
{
    float total = 0, weight = .53;
    [loop] for (int i=0; i<octaves; ++i)
    {
        total += weight * Grain(p);
        p = 2.07 * float2(dot(p,float2(.8,.6)),dot(p,float2(-.6,.8))) + float2(31.7,8.9);
        weight *= .49;
    }
    return total;
}

// Small eddies deform the coordinates of successively larger ones. A broad
// bank therefore inherits broken, branching edges all the way down to a few pixels.
float CloudPotential(float2 pos, float seconds)
{
    float2 parcel = pos * 4.7 + float2(12.7,-7.2) + seconds * float2(.18,-.12);
    float frequency = 58.45; // 1.97 to the sixth power
    [unroll] for (int octave=6; octave>=0; --octave)
    {
        float sn,cs;
        sincos(1.13*octave+.7,sn,cs);
        float2 axis=float2(cs,sn), side=float2(-sn,cs);
        float2 q=frequency*float2(dot(parcel,axis),dot(parcel,side)) + octave*float2(11.2,13.3);
        q += seconds * (.15+.041*octave) * float2(cs-sn,sn+cs);
        float2 deflect=float2(Grain(q),Grain(q+float2(14.1,17.7)))-.5;
        parcel += (1.04/frequency)*(axis*deflect.x + side*deflect.y);
        frequency /= 1.97;
    }
    float2 roll=float2(FractalPotential(parcel*.81+float2(-3,8),3),
                       FractalPotential(parcel*.81+float2(11,-4),3))-.43;
    parcel += 2.5*roll;
    float cloud = FractalPotential(parcel,5);
    float rim = saturate(1-6*abs(cloud-.48));
    float scallops = FractalPotential(parcel*3.8+float2(8,-3),4);
    cloud += .044*rim*rim*rim + .22*(scallops-.47);
    return saturate(.48 + 2.55*(cloud-.48));
}

float4 CloudBody(float2 pixel)
{
    float2 uv = (pixel + .5) / size;
    float2 aspect = float2(size.y/(float)size.x,1);
    // Reconstruct the volume from local masses, rather than tracing the
    // outlines of stretched ink. The cloud field supplies fine-scale structure.
    float4 body = .16*cloudDensity.SampleLevel(linearClamp,uv,0);
    [unroll] for (int i=0; i<8; ++i)
    {
        float sn,cs;
        sincos((i+.4)*.78539816,sn,cs);
        float2 v = float2(cs,sn)*aspect;
        body += .061*cloudDensity.SampleLevel(linearClamp,uv + v*.046,0);
        body += .044*cloudDensity.SampleLevel(linearClamp,uv + v*.109,0);
    }
    // Redistribute part of the existing optical mass across the window. The
    // conserved mean reveals continuous detail at the borders without adding dye.
    float4 mean = relief.Load(int3(0,0,0)) / (size.x * (float)size.y);
    body = lerp(body,mean,.48);
    float cloud = simulatedCloud.SampleLevel(linearClamp,uv,0).x;
    // Side-light the simulated edges at two scales. The same scalar controls
    // both the shadow and its rim as each branch moves with the pigment.
    float2 toward = float2(.82*aspect.x,-.57);
    float fineShade = cloud - simulatedCloud.SampleLevel(linearClamp,uv - toward*.004,0).x;
    float broadShade = cloud - simulatedCloud.SampleLevel(linearClamp,uv - toward*.017,0).x;
    float3 ink = body.rgb / max(.00001,body.a);
    float light = clamp(1.86*cloud-.08 + .64*fineShade + .46*broadShade,.02,1.52);
    body.rgb = body.a * saturate(smokePaper.rgb + light*(ink-smokePaper.rgb));
    float fresh=freshFlowInk.SampleLevel(linearClamp,uv,0).a;
    // Newly released letter pigment keeps its outline briefly as it travels
    // into the larger cloud. The tag fades; its pigment remains in the field.
    return lerp(body,Read(source,int2(pixel)),saturate(fresh*2.2));
}

float4 CubicDye(float4 a, float4 b, float4 c, float4 d, float t)
{
    return b + 0.5 * t * ((c - a) + t * (2 * a - 5 * b + 4 * c - d + t * (3 * (b - c) + d - a)));
}

// Limit the cubic reconstruction to its four donor cells. This preserves
// thin folds during a long hold without ringing or introducing fresh dye.
float4 HeldDye(float2 pos)
{
    int2 base = int2(floor(pos));
    float2 f = frac(pos);
    float4 rows[4];
    float4 low = 0, high = 0;
    [unroll] for (int y = 0; y < 4; ++y)
    {
        int2 k = base + (y - 1) * Y;
        float4 b = Read(source, k), c = Read(source, k + X);
        rows[y] = CubicDye(Read(source, k - X), b, c, Read(source, k + 2 * X), f.x);
        if (y == 1) { low = min(b, c); high = max(b, c); }
        if (y == 2) { low = min(low, min(b, c)); high = max(high, max(b, c)); }
    }
    return clamp(CubicDye(rows[0], rows[1], rows[2], rows[3], f.y), low, high);
}

float3 WaterBasin(float2 uv)
{
    float2 p = uv * float2(1.77778, 1);
    // Deep water lies opposite the angle's direction. 13 degrees is the original layout.
    float2 toward = 0.842 * float2(sin(radians(waterAngle)), cos(radians(waterAngle)));
    float gradient = saturate(0.555 + dot(uv - 0.5, toward) + 0.18 * (Grain(p * 3.8) - 0.5));
    float3 deep = waterDeep.rgb;
    float3 shallow = waterShallow.rgb;

    // Keep the mottling fixed to the basin so the surface bends it naturally.
    float2 warp = 0.075 * float2(Grain(p * 7.2 + 8), Grain(p * 7.2 + 23));
    float2 q = p + warp;
    float cloud = 0.57 * Grain(q * 11) + 0.28 * Grain(q * 29) + 0.15 * Grain(q * 66);
    float2 fineUV = float2(q.x + 0.32 * q.y, q.y - 0.25 * q.x);
    float2 speckPlace = fineUV * float2(290, 185) * specksFine;
    float grain = Grain(speckPlace);
    float speckArea = smoothstep(0.41, 0.71, Grain(q * 52 + 4));
    float specks = smoothstep(0.70, 0.94, grain) * speckArea;
    // A soft halo around each speck.
    float halo = smoothstep(0.45, 0.94, grain) * speckArea;
    // Each speck flashes on its own rhythm, like light catching a facet.
    float facet = GrainHash(floor(speckPlace));
    float flash = pow(saturate(sin(6.2831853 * (facet + time * (0.35 + 0.9 * facet)))), 6);
    float shine = lerp(1, 3 * flash, specksTwinkle);
    float patina = (cloud - 0.5) * 0.21 + (Grain(fineUV * float2(290, 185)) - 0.5) * 0.041;
    return lerp(deep, shallow, smoothstep(0, 1, gradient)) + float3(0.63, 0.90, 0.91) * patina +
           specksColor.rgb * (waterSpecks * shine * (specks + specksGlow * 0.6 * halo));
}

float3 InkColor()
{
    // Each deposit is a different step from the background color toward the ink color.
    return lerp(smokePaper.rgb, smokeCurrent.rgb, lerp(smokeShadeLow, smokeShadeHigh, saturate(ink / 31)));
}

// The Ink view stores how much light the ink absorbs, as watercolor does. An ink value of 100 or more
// selects it, and ink - 100 picks one of its five colors.
bool Absorbing()
{
    return ink >= 100;
}

float3 Absorbance()
{
    float3 color = inkColors[(uint)(ink - 100) % 5].rgb;
    return -log(max(color, 0.012));
}

uint Scramble(uint h)
{
    h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
    return h;
}

// Whether a particle is among the share picked by a keystroke. The pick is spread over the whole swarm.
bool Chosen(uint index, uint seed, float share)
{
    float pick = (float)(Scramble((index * 2654435761u) ^ (seed * 40503u)) & 0xffffffu);
    return pick < share * 16777216.0;
}

float4 LayInk(float4 existing, float coverage, int2 p)
{
    // Seed texture only when pigment is laid down. Color and absorbance carry it through
    // the same advection, diffusion and resize passes as the ink, with no screen-fixed overlay.
    float2 place = (p + 0.5) / size.y;
    float2 seed = phase * float2(7.13, 3.71);
    float grain = Grain(place * 380 + seed) - 0.5;
    float cloud = 0.65 * Grain(place * 4 + seed) + 0.35 * Grain(place * 13 + seed) - 0.5;
    float textureWeight = max(0.05, 1 + smokeGrain * (0.11 * grain + 0.12 * cloud) / 0.895);
    // Absorbing ink adds up. Smoke's ink is laid over what is already there.
    float covered = saturate(coverage);
    float4 laid = float4(saturate(InkColor() * textureWeight) * covered, covered) + existing * (1 - covered);
    float4 absorbed = existing + float4(Absorbance() * textureWeight, 1) * coverage * smokeGlow;
    return Absorbing() ? absorbed : laid;
}

[numthreads(8, 8, 1)]
void Fluid(uint3 id : SV_DispatchThreadID)
{
    int2 p = int2(id.xy);
    if (any(p >= size)) return;
    float4 center = Read(source, p);
    float4 left = Read(source, p - X), right = Read(source, p + X);
    float4 top = Read(source, p - Y), bottom = Read(source, p + Y);
    float4 value = 0;

    switch (operation)
    {
    case 0: // Transport and damp the velocity.
        {
            float2 q = (p + 0.5) * (2.6 / size.y) + time * float2(0.03, 0.02);
            float2 dx = float2(0.052, 0), dy = dx.yx;
            float2 drift = float2(Grain(q + dy) - Grain(q - dy), Grain(q - dx) - Grain(q + dx));
            float2 velocity = Sample(source, p - center.xy * smokeSpeed).xy * smokeKeep + smokeDrift * drift;
            if (heldTransport > 0 && smokeTurbulence > 0)
            {
                float2 uv = (p + 0.5) / size.y;
                float2 currents =
                    1.30 * ObliqueCurl(uv * 3.4, float2(.8,.6), float2(.27,-.19) * time + 11) +
                    0.73 * ObliqueCurl(uv * 7.7, float2(-.6,.8), float2(-.31,-.23) * time + 29) +
                    0.32 * ObliqueCurl(uv * 18.3, float2(.384615,.923077), float2(.36,.42) * time + 53);
                velocity += smokeTurbulence * currents;
            }
            else if (smokeTurbulence > 0)
            {
                float2 uv = (p + 0.5) / size.y;
                float2 driftTime = time * float2(0.31, -0.22);
                velocity += smokeTurbulence * (2.75 * NoiseCurl(uv * 6 + driftTime) +
                    2.35 * NoiseCurl(uv * 17 - driftTime * 1.7 + 19) +
                    1.55 * NoiseCurl(uv * 43 + driftTime * 3.5 + 37));
            }
            value.xy = WallVelocity(velocity, p);
        }
        break;
    case 1: // Curl and divergence in a single pass.
        {
            float l = p.x == 0 ? -center.x : left.x;
            float r = p.x == size.x - 1 ? -center.x : right.x;
            float t = p.y == 0 ? -center.y : top.y;
            float b = p.y == size.y - 1 ? -center.y : bottom.y;
            value.xy = 0.5 * float2(right.y - left.y - bottom.x + top.x, r - l + b - t);
        }
        break;
    case 2: // Preserve rolling edges with vorticity confinement.
        {
            float c = Read(field, p).x;
            float2 gradient = float2(abs(Read(field, p + X).x) - abs(Read(field, p - X).x),
                                     abs(Read(field, p + Y).x) - abs(Read(field, p - Y).x));
            float2 force = float2(gradient.y, -gradient.x) / (length(gradient) + 0.0002);
            value.xy = WallVelocity(clamp(center.xy + force * c * smokeCurl, -smokeLimit, smokeLimit), p);
        }
        break;
    case 3: // Jacobi pressure solve.
        value.x = 0.25 * (left.x + right.x + top.x + bottom.x - Read(field, p).y);
        break;
    case 4: // Project the velocity toward an incompressible flow.
        {
            float2 gradient = float2(Read(field, p + X).x - Read(field, p - X).x,
                                     Read(field, p + Y).x - Read(field, p - Y).x);
            value.xy = WallVelocity(center.xy - 0.5 * gradient, p);
        }
        break;
    case 5: // Transport premultiplied color and coverage together.
        {
            // Distances are tuned for 720 dye rows and scale with the window.
            float reach = 1.5 * size.y / 720.0;
            float2 back = p - PigmentVelocity(p) * smokeSpeed;
            if (heldTransport > 0)
            {
                back = relief.SampleLevel(linearClamp,(p+.5)/size,0).xy * size - .5;
                value = max(abs(back.x-p.x),abs(back.y-p.y)) < .001 ? center : HeldDye(clamp(back, 0, size - 1));
                break;
            }
            float4 dye = Sample(source, back);
            float4 around = Sample(source, back - reach * X) + Sample(source, back + reach * X) +
                            Sample(source, back - reach * Y) + Sample(source, back + reach * Y);
            value = lerp(dye, around * 0.25, smokeSpread) * smokeFade;
        }
        break;
    case 6: // A Gaussian brush stroke drags the water along a short segment.
        {
            float2 direction = float2(cos(phase), sin(phase));
            float2 local = (p - origin) / radius;
            float2 offset = local - direction * clamp(dot(local, direction), -0.65, 0.65);
            float weight = 0.60 * exp(-dot(offset, offset));
            value.xy = WallVelocity(lerp(center.xy, strength * direction, weight), p);
        }
        break;
    case 7: // Release an uneven wash in the shape of the committed word.
        {
            float coverage = Read(field, p).a;
            float2 paper = p * (720.0 / size.y);
            float wash = 0.56 + 0.25 * Grain(paper * 0.018 + phase) + 0.08 * Grain(paper * 0.11);
            value = LayInk(center, coverage * wash * (Absorbing() ? 1 : smokeAmount), p);
        }
        break;
    case 8: // Show transported pigment, in WPF's premultiplied BGRA byte order.
        {
            float blurWeight=0;
            if(readingBlurControl.x>.001 && !Absorbing())
            {
                float2 uv=(p+.5)/size;
                float vertical=abs(uv.y-.5)/max(.02,readingBlurControl.y);
                float horizontal=1-smoothstep(.30,.52,abs(uv.x-.5));
                blurWeight=readingBlurControl.x*exp(-vertical*vertical*vertical*vertical)*horizontal;
                if(blurWeight>.002)
                {
                    float radius=readingBlurControl.z*size.y;
                    float2 dx=float2(radius,0),dy=float2(0,radius);
                    float2 a=radius*.7071*float2(1,1),b=a*float2(1,-1);
                    float4 blurred=.28*center+
                        .12*(Sample(source,p+dx)+Sample(source,p-dx)+Sample(source,p+dy)+Sample(source,p-dy))+
                        .06*(Sample(source,p+a)+Sample(source,p-a)+Sample(source,p+b)+Sample(source,p-b));
                    center=lerp(center,blurred,blurWeight);
                }
            }
            float edge = length(float2(length(right - left), length(bottom - top)))*(1-blurWeight);
            // Keep the old average brightness. All fine texture now lives in the pigment.
            float shading = 0.895 + 0.16 * saturate(4 * edge);
            if (Absorbing())
            {
                // Paper times the light the ink lets through. Overlapping inks darken, and faded ink returns to paper.
                float3 paperColor = inkPaper.rgb * exp(-max(center.rgb, 0));
                value = float4(saturate(paperColor).bgr, 1);
            }
            else
            {
                if (cloudLighting > 0 && center.a > 0.001)
                {
                    float2 uv = (p + 0.5) / size.y;
                    float2 travel = time * float2(0.19, -0.14);
                    float2 place = uv + 0.026 * NoiseCurl(uv * 14 + travel);
                    float cloud = 0.57 * Grain(place * 11 + travel) +
                        0.28 * Grain(place * 27 - travel * 2) + 0.15 * Grain(place * 64 + travel * 4);
                    shading += cloudLighting * (cloud - 0.58) * 0.86;
                }
                float detail = smokePaper.w;
                if (detail > 0)
                {
                    uint width,height;
                    cloudDensity.GetDimensions(width,height);
                    if (width > 0)
                    {
                        center = lerp(center, CloudBody(p), detail);
                        // The evolving cloud field supplies the interior relief. Lighting raw dye
                        // gradients here would outline the underlying smooth ribbons.
                        shading = lerp(shading,.94,detail);
                    }
                }
                value = float4(min(center.bgr * shading, center.a), center.a) * smokeGlow;
                if (colorGradientControl.x > 0 && value.a > .0001)
                {
                    float2 uv = (p + .5) / size - .5;
                    float2 direction = colorGradientControl.yz;
                    float span = max(.001, abs(direction.x) + abs(direction.y));
                    float blend = smoothstep(0, 1, .5 + dot(uv, direction) / span);
                    float3 tint = lerp(colorStart.rgb, colorEnd.rgb, blend);
                    float3 original = value.bgr / value.a;
                    const float3 luminance = float3(.2126, .7152, .0722);
                    // Change chroma, preserving each pixel's original luminance
                    // and coverage. No spatial filtering or texture flattening.
                    float3 shifted = tint + dot(original - tint, luminance);
                    value.rgb = lerp(value.rgb, saturate(shifted).bgr * value.a, colorGradientControl.x);
                }
                if (heldTransport > 0 && value.a > 0)
                {
                    // Dither the final 8-bit pigment surface only. The static sub-level
                    // variation breaks up contours without feeding noise into the dye.
                    float jitter = frac(52.9829189 * frac(dot(p, float2(.06711056, .00583715)))) - .5;
                    value.rgb = clamp(value.rgb + jitter / 255, 0, value.a);
                }
            }
        }
        break;
    case 10: // Typing's outline stretch and recoil.
        value.xy=WallVelocity(clamp(center.xy+OutlineImpulse(p,previousScale,entranceScale,size*.5),
                                   -smokeLimit,smokeLimit),p);
        break;
    case 9: // A short, irregular pigment trail follows the stroke.
        {
            float2 direction = float2(cos(phase), sin(phase));
            float2 local = (p - origin) / radius;
            float2 offset = local - direction * clamp(dot(local, direction), -0.65, 0.65);
            float distance = length(offset) + 0.20 * (Grain(p * (720.0 / size.y) * 0.08 + phase) - 0.5);
            float coverage = 1 - smoothstep(0.13, 0.38, distance);
            value = LayInk(center, coverage * 0.30 * smokeWake, p);
        }
        break;
    case 11:
        value.x = 0.8 * center.x;
        break;
    case 12: // Release the smooth word relief into the travelling height field.
        {
            float lap = left.x + right.x + top.x + bottom.x - 4 * center.x;
            float glyph = Read(field, p).x;
            float boundary = min(min(p.x, size.x - 1 - p.x), min(p.y, size.y - 1 - p.y));
            float damping = lerp(0.78, 0.963, smoothstep(0, 26, boundary));
            float h = 0.999 * (center.x + damping * (center.x - center.y) + 0.28 * lap);
            if (ink > 0)
            {
                float2 uv = (p + 0.5) / size;
                float nextShape = field.SampleLevel(linearClamp, VibratingWordUV(uv, time), 0).x;
                float lastShape = field.SampleLevel(linearClamp, VibratingWordUV(uv, time - previousScale.x), 0).x;
                h += 0.014 * (nextShape - lastShape);
            }
            // Add equal displacement to both states so the initial shape has no velocity spike.
            float release = strength * glyph;
            value.xy = float2(h, center.x) + release;
        }
        break;
    case 13: // Refract a quiet blue basin beneath the held word and travelling ripples.
        {
            float2 uv = (p + 0.5) / size;
            float2 cell = velocityScale / size;
            float2 dx = float2(cell.x, 0), dy = float2(0, cell.y);
            float h = Sample(source, p).x;
            float l = source.SampleLevel(linearClamp, uv - dx, 0).x;
            float r = source.SampleLevel(linearClamp, uv + dx, 0).x;
            float b = source.SampleLevel(linearClamp, uv + dy, 0).x;
            float t = source.SampleLevel(linearClamp, uv - dy, 0).x;
            if (ink > 0)
            {
                h += HeldWordHeight(uv);
                l += HeldWordHeight(uv - dx);
                r += HeldWordHeight(uv + dx);
                b += HeldWordHeight(uv + dy);
                t += HeldWordHeight(uv - dy);
            }
            float lap = l + r + b + t - 4 * h;
            float2 slope = waterBend * float2(r - l, b - t) + WaterDriftSlope(uv);
            float3 n = normalize(float3(-slope.x, slope.y, 1));
            float3 ray = refract(float3(0, 0, -1), n, 1.0 / 1.333);
            float2 floorUV = uv + (ray.xy / max(0.40, -ray.z)) * float2(0.5625, -1) * (waterDepth + 0.013 * h);
            float3 color = WaterBasin(floorUV);
            float light = CausticLight(floorUV);
            float start = 1.65 * causticThreshold, middle = 2.7 * causticThreshold;
            float caustics = 0.16 * smoothstep(start, start + 1.05 / causticSharp, light) +
                             0.84 * smoothstep(middle, middle + 4.7 / causticSharp, light);
            color += waterCaustic.rgb * (waterCaustics * caustics + clamp(-lap * 2.8, -0.04, 0.12));
            float key = pow(saturate(dot(n, normalize(float3(-0.31, 0.32, 1)))), 38);
            float rim = pow(saturate(dot(n, normalize(float3(0.40, -0.20, 1)))), 85);
            color += waterHighlight.rgb * (0.36 * key + 0.18 * rim);
            value = float4(saturate(color.bgr), 1);
        }
        break;

    case 14:
        {
            float total = 0;
            int width = (int)radius;
            for (int x = -width; x <= width; ++x)
                total += Read(source, p + x * X).a * (width + 1 - abs(x));
            value.x = total / ((width + 1) * (width + 1));
        }
        break;
    case 15:
        {
            float total = 0;
            int width = (int)radius;
            for (int y = -width; y <= width; ++y)
                total += Read(source, p + y * Y).x * (width + 1 - abs(y));
            value.x = total / ((width + 1) * (width + 1));
        }
        break;
    case 16: // Reflect broad studio lights from near-black liquid.
        {
            float2 uv = (p + 0.5) / size;
            float2 dx = float2(velocityScale.x, 0), dy = float2(0, velocityScale.y);
            float l = Sample(source, p - dx).x, r = Sample(source, p + dx).x;
            float t = Sample(source, p - dy).x, b = Sample(source, p + dy).x;
            float height = Sample(source, p).x;
            if (ink > 0)
            {
                l += HeldWordHeight(uv - dx / size);
                r += HeldWordHeight(uv + dx / size);
                t += HeldWordHeight(uv - dy / size);
                b += HeldWordHeight(uv + dy / size);
                height += HeldWordHeight(uv);
            }
            float3 normal = normalize(float3(-8.4 * float2(r - l, b - t), 1));
            // Slightly oblique viewing rays let the reflection slide over the curved rim.
            float2 q = uv - 0.5;
            float3 view = normalize(float3(-q.x * 0.38, 0.19 - q.y * 0.29, 1));
            float3 reflected = reflect(-view, normal);
            float key = pow(saturate(dot(reflected, normalize(float3(-0.37, -0.48, 1)))), 24);
            float rim = pow(saturate(dot(reflected, normalize(float3(0.48, 0.32, 1)))), 48);
            float box = exp(-8 * abs(reflected.y + 0.36) - 2.4 * abs(reflected.x + 0.18));
            float fresnel = pow(saturate(1 - dot(normal, view)), 3);
            float room = 0.055 + 0.035 * exp(-7 * dot(q * float2(1.15, 1), q));
            float body = 0.036 * smoothstep(0.015, 0.65, height);
            float light = room + body + 0.38 * key + 0.43 * rim + 0.29 * box + 0.24 * fresnel;
            value = float4(light * float3(1.10, 1.01, 0.92), 1);
        }
        break;
    case 17: // WordCraftViz waveStep.
        {
            const float wordWaveGain = waterGain;
            float2 uv = (p + 0.5) / size;
            float2 e = 3.0 / size;
            float h = center.x + (center.x - center.y) * waterKeep +
                      waterSpeed * (left.x + right.x + bottom.x + top.x - 4 * center.x);
            if (phase != 0)
            {
                float current = 0.5 * (WaterMaskCross(field, uv, e) + WaterMaskRing(field, uv, e * 2));
                float previous = 0.5 * (WaterMaskCross(relief, uv, e) + WaterMaskRing(relief, uv, e * 2));
                h += wordWaveGain * phase * (current - previous);
            }
            if (strength != 0)
            {
                float current = WaterMaskRing(field, uv, e * 2) + WaterMaskRing(field, uv, e * 3.5);
                float previous = WaterMaskRing(relief, uv, e * 2) + WaterMaskRing(relief, uv, e * 3.5);
                h += wordWaveGain * strength * 0.5 * (phase != 0 ? max(current, previous) : current);
            }
            if (ink > 0)
            {
                float now = WaterMask(field, VibratingWordUV(uv, time));
                float before = WaterMask(field, VibratingWordUV(uv, time - previousScale.x));
                h += wordWaveGain * 0.009 * waterFlutter * (now - before);
            }
            float2 d = (uv - origin) * float2((float)size.x / size.y, 1);
            h += radius * exp(-dot(d, d) / (0.0012 * waterDropSize * waterDropSize));
            value.xy = float2(clamp(h * 0.9992, -4, 4), center.x);
        }
        break;
    case 18: // Carry dye and motion into grids of a new size.
        value = source.SampleLevel(linearClamp, (p + 0.5) / size, 0);
        break;

    // The Fluid, Fire and Particles views share the passes below. Velocity is in flow cells per step.
    case 20: // Vorticity confinement with a per-view strength.
        {
            float c = Read(field, p).x;
            float2 gradient = float2(abs(Read(field, p + X).x) - abs(Read(field, p - X).x),
                                     abs(Read(field, p + Y).x) - abs(Read(field, p - Y).x));
            float2 force = float2(gradient.y, -gradient.x) / (length(gradient) + 0.0002);
            // radius carries the view's speed limit.
            value.xy = WallVelocity(clamp(center.xy + force * c * strength, -radius, radius), p);
        }
        break;
    case 21: // Carry the velocity along itself. Heat in the dye's red channel lifts the flow.
        {
            // previousScale.x is the view's flow speed.
            float2 velocity = Sample(source, p - center.xy * previousScale.x).xy * strength;
            velocity.y -= phase * field.SampleLevel(linearClamp, (p + 0.5) / size, 0).x;
            value.xy = WallVelocity(velocity, p);
        }
        break;
    case 22: // Carry the dye along the flow and let it fade.
        value = max(Sample(source, p - PigmentVelocity(p)) * strength, 0);
        break;
    case 23: // Add a soft round blob of dye.
        {
            float2 offset = (p + 0.5 - origin) / radius;
            value = center + exp(-dot(offset, offset)) * strength * float4(previousScale, entranceScale.x, 0);
        }
        break;
    case 24: // Push the fluid around a point.
        {
            float2 offset = (p + 0.5 - origin) / radius;
            value.xy = WallVelocity(center.xy + exp(-dot(offset, offset)) * previousScale, p);
        }
        break;
    case 25: // Lay dye in the shape of the word. Phase 0 adds it. Phase 1 keeps whichever is stronger, so the
             // newest word is always the brightest thing on screen. Phase 2 replaces what is there, so a dark
             // version of a color can show over bright dye.
        {
            float cover = Read(field, p).a;
            float4 color = strength * float4(previousScale, entranceScale.x, 0);
            if (phase > 1.5)
                value = lerp(center, color, cover);
            else if (phase > 0.5)
                value = max(center, color * cover);
            else
                value = center + color * cover;
        }
        break;
    case 26: // Push outward from the word's outline, with an optional upward lift inside it.
        {
            // radius sets how far from the letters' edges the push reaches.
            int reach = max(1, (int)round(radius));
            float2 outward = -float2(Read(field, p + reach * X).a - Read(field, p - reach * X).a,
                                     Read(field, p + reach * Y).a - Read(field, p - reach * Y).a) * (3.0 / reach);
            value.xy = WallVelocity(center.xy + outward * strength - float2(0, phase) * Read(field, p).a, p);
        }
        break;
    case 27: // Glowing dye on deep navy.
        {
            float3 dye = max(center.rgb, 0);
            float3 slope = normalize(float3(length(right.rgb) - length(left.rgb),
                                            length(bottom.rgb) - length(top.rgb), 0.02));
            dye *= clamp(slope.z + 0.7, 0.7, 1);
            float3 halo = 0;
            float reach = size.y * 0.02;
            // A different turn at every pixel spreads the glow as fine grain, with no ghost copies of sharp edges.
            float turn = 6.2831853 * GrainHash(float2(p));
            for (int i = 0; i < 8; ++i)
            {
                float2 direction = float2(cos(turn + 0.7853982 * i), sin(turn + 0.7853982 * i));
                halo += max(Sample(source, p + direction * reach).rgb, 0) +
                        max(Sample(source, p + direction * reach * 2.4).rgb, 0);
            }
            float3 light = 1 - exp(-(dye + halo * (strength / 16)) * phase);
            float3 navy = fluidBackground.rgb;
            value = float4(saturate(navy * (1 - max(light.r, max(light.g, light.b))) + light).bgr, 1);
        }
        break;
    case 30: // Particles lag behind the fluid, so spinning flow throws them outward and opens voids.
        {
            float2 fluid = field.SampleLevel(linearClamp, center.xy, 0).xy * previousScale;
            float2 motion = lerp(center.zw, fluid, strength);
            // Grid magnetics: each particle drifts back toward its place in the starting grid. ink is the pull.
            float2 home = (p + 0.5) / size;
            float2 place = center.xy + motion + (home - center.xy) * ink;
            if (place.x < 0 || place.x > 1) motion.x = -motion.x;
            if (place.y < 0 || place.y > 1) motion.y = -motion.y;
            value = float4(saturate(place), motion);
        }
        break;
    case 33: // Move a random share of the whole swarm into the word, so each keystroke draws it in light.
             // origin.x is the outline's thickness in mask pixels, or 0 to fill the letters. origin.y is the share,
             // ink the keystroke's seed.
        {
            uint index = (uint)p.y * (uint)size.x + (uint)p.x;
            value = center;
            if (Chosen(index, (uint)ink, origin.y))
            {
                uint width, height;
                field.GetDimensions(width, height);
                float2 reach = max(origin.x, 0.5) / float2(width, height);
                for (uint k = 0; k < 32; ++k)
                {
                    uint h = Scramble(index * 747796405u + k * 2891336453u + (uint)ink * 277803737u);
                    float2 pick = float2(h & 0xffffu, h >> 16) / 65535.0;
                    float2 place = lerp(previousScale, entranceScale, pick);
                    // Inside the letter, and with a thickness, on its outline: outside within reach on some side.
                    if (field.SampleLevel(linearClamp, place, 0).a > 0.5 && (origin.x <= 0 ||
                        min(min(field.SampleLevel(linearClamp, place + float2(reach.x, 0), 0).a,
                                field.SampleLevel(linearClamp, place - float2(reach.x, 0), 0).a),
                            min(field.SampleLevel(linearClamp, place + float2(0, reach.y), 0).a,
                                field.SampleLevel(linearClamp, place - float2(0, reach.y), 0).a)) < 0.5))
                    {
                        value = float4(place, 0, 0);
                        break;
                    }
                }
            }
        }
        break;
    case 34: // Particle glow: the keystroke's chosen particles take its color at full strength, then dim each step.
             // Color is stored already multiplied by the glow, which sits in alpha.
        {
            uint index = (uint)p.y * (uint)size.x + (uint)p.x;
            value = Chosen(index, (uint)ink, origin.y) ? float4(particleGlow.rgb, 1) : center * strength;
        }
        break;
    case 35: // Every particle inside a letter is thrown out toward the letter's nearest edge, so each keystroke
             // opens the whole word as voids. strength is the throw in window widths per step.
        {
            value = center;
            float inside = field.SampleLevel(linearClamp, center.xy, 0).a;
            if (inside > 0.5)
            {
                uint width, height;
                field.GetDimensions(width, height);
                float2 texel = 1.0 / float2(width, height);
                // Compare the letter on either side at several distances, so the middle of a thick stroke
                // still knows which edge is nearer.
                float2 slope = 0;
                for (int ring = 1; ring <= 4; ++ring)
                {
                    float2 r = texel * (3.0 * (float)(ring * ring));
                    slope += float2(field.SampleLevel(linearClamp, center.xy + float2(r.x, 0), 0).a -
                                    field.SampleLevel(linearClamp, center.xy - float2(r.x, 0), 0).a,
                                    field.SampleLevel(linearClamp, center.xy + float2(0, r.y), 0).a -
                                    field.SampleLevel(linearClamp, center.xy - float2(0, r.y), 0).a) / (float)ring;
                }
                uint h = Scramble((uint)(center.x * 65535.0) * 747796405u + (uint)(center.y * 65535.0) * 2891336453u);
                float turn = (float)(h & 0xffffu) / 65535.0 * 6.2831853;
                // Where the sides balance, such as the exact middle of a stroke, pick a random direction.
                float2 outward = length(slope) > 0.02 ? -normalize(slope) : float2(cos(turn), sin(turn));
                // A little spread keeps the voids' edges soft and alive.
                float spread = 0.75 + 0.5 * ((float)(h >> 16) / 65535.0);
                value.zw = outward * strength * spread * float2(height / (float)width, 1);
            }
        }
        break;
    case 36: // The whole word pushes the fluid outward from inside every letter at once, toward each letter's
             // nearest edge. field is the word at flow resolution, strength is in flow cells per step and radius
             // is the speed limit.
        {
            float2 uv = (p + 0.5) / size;
            value = center;
            float inside = field.SampleLevel(linearClamp, uv, 0).a;
            if (inside > 0.5)
            {
                uint width, height;
                field.GetDimensions(width, height);
                float2 texel = 1.0 / float2(width, height);
                float2 slope = 0;
                for (int ring = 1; ring <= 4; ++ring)
                {
                    float2 r = texel * (float)(ring * ring);
                    slope += float2(field.SampleLevel(linearClamp, uv + float2(r.x, 0), 0).a -
                                    field.SampleLevel(linearClamp, uv - float2(r.x, 0), 0).a,
                                    field.SampleLevel(linearClamp, uv + float2(0, r.y), 0).a -
                                    field.SampleLevel(linearClamp, uv - float2(0, r.y), 0).a) / (float)ring;
                }
                float2 outward = length(slope) > 0.02 ? -normalize(slope) : float2(0, 0);
                value.xy = WallVelocity(clamp(center.xy + outward * strength * inside, -radius, radius), p);
            }
        }
        break;
    case 37: // GPU reduction of premultiplied pigment, including partial edge tiles.
        {
            uint width, height;
            source.GetDimensions(width,height);
            int2 start = p*8;
            [loop] for (int y=start.y; y<min(start.y+8,(int)height); ++y)
                [loop] for (int x=start.x; x<min(start.x+8,(int)width); ++x)
                    value += source.Load(int3(x,y,0));
        }
        break;
    case 38: // Reconcile with the pigment present when Enter was pressed.
        {
            float4 live = field.Load(int3(0,0,0)), original = relief.Load(int3(0,0,0));
            float4 gain = clamp(original/max(live,.0001),.97,1.03);
            value = saturate(center * gain);
            value.rgb = min(value.rgb,value.a);
        }
        break;
    case 39: // One shared departure map carries both pigment and its fine structure.
        value.xy = (HeldDeparture(p)+.5)/size;
        break;
    case 40: // Coarse optical density for the cloud body, rebuilt each frame.
        {
            [unroll] for (int y=0;y<4;++y)
                [unroll] for (int x=0;x<4;++x)
                    value += source.SampleLevel(linearClamp,(p+(float2(x,y)+.5)/4)/size,0)/16;
        }
        break;
    case 41: // Carry the persistent cloud through the same map as the pigment.
        {
            float2 back = relief.SampleLevel(linearClamp,(p+.5)/size,0).xy * size - .5;
            value.xy = max(abs(back.x-p.x),abs(back.y-p.y)) < .001 ? center.xy : HeldDye(clamp(back,0,size-1)).xy;
        }
        break;
    case 42: // Seed once. Subsequent frames only transport the existing branches.
        value.xy = float2(CloudPotential((p+.5)/size.y,0),1);
        break;
    case 43: // Receive the exact disappearing letter coverage as typing pigment.
        {
            float coverage=Read(field,p).a;
            value=LayInk(center,coverage*strength,p);
        }
        break;
    case 47: // One-time procedural ambient cloud, before any text exists.
        {
            float cloud = CloudPotential((p + .5) / size.y, 0);
            float coverage = .08 + .72 * smoothstep(.18, .80, cloud);
            value = LayInk(0, coverage, p);
        }
        break;
    case 48: // Give the initial cloud a gentle, multiscale curl.
        {
            float2 pos = (p + .5) / size.y;
            value.xy = WallVelocity(.48 * NoiseCurl(pos * 5.3 + 13) +
                .21 * ObliqueCurl(pos * 13.7, float2(.8,.6), 31), p);
        }
        break;
    case 44: // Carry the fresh-letter tag with its pigment as its sharpness relaxes.
        {
            float2 back=heldTransport>0 ? relief.SampleLevel(linearClamp,(p+.5)/size,0).xy*size-.5
                : p-PigmentVelocity(p)*smokeSpeed;
            value=HeldDye(clamp(back,0,size-1))*.965;
        }
        break;
    case 45: // The same outline recoil, centered on the released Flow glyphs.
        value.xy=WallVelocity(clamp(center.xy+OutlineImpulse(p,previousScale,entranceScale,origin),
                                   -smokeLimit,smokeLimit),p);
        break;
    case 46: // Opacity-weighted typing impulse plus inherited letter motion.
        {
            float2 direction=float2(cos(phase),sin(phase));
            float2 local=(p-origin)/max(1,radius);
            float2 offset=local-direction*clamp(dot(local,direction),-.65,.65);
            float weight=.60*exp(-dot(offset,offset))*saturate(entranceScale.x);
            float coverage=field.SampleLevel(linearClamp,(p+.5)/size,0).a;
            float2 carried=clamp(previousScale,-smokeLimit,smokeLimit)*coverage*.5;
            value.xy=WallVelocity(clamp(center.xy+strength*direction*weight+carried,
                                       -smokeLimit,smokeLimit),p);
        }
        break;
    case 31: // Start the particles as an even grid over the window.
        value = float4((p + 0.5) / size, 0, 0);
        break;
    case 32: // Particle light over a faint dye tint on near-black.
        {
            float3 light = 1 - exp(-max(center.rgb, 0) * phase);
            float3 tint = 1 - exp(-max(Read(field, p).rgb, 0) * strength);
            value = float4(saturate(particleBackground.rgb + tint * 0.55 + light).bgr, 1);
        }
        break;

    }
    result[p] = value;
}
