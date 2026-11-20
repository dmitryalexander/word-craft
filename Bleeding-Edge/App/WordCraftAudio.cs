using System;
using System.Threading;

sealed class WordCraftAudio : IDisposable
{
    readonly DialUpMorse typing;
    readonly NoiseGenerator noise=new NoiseGenerator();
    readonly MorseOutput noiseOutput;
    double volume=1,morseVolume=1;
    static double Gain(double value){return Double.IsNaN(value)||Double.IsInfinity(value)?0:Math.Max(0,Math.Min(1,value));}
    internal WordCraftAudio(bool enable=true){typing=new DialUpMorse(enable);noiseOutput=new MorseOutput(enable,noise.Fill);ApplyVolumes();}
    void ApplyVolumes(){typing.Volume=volume*morseVolume;noise.Master=volume;}
    internal double Volume {get{return volume;}set{volume=Gain(value);ApplyVolumes();}}
    internal double MorseVolume {get{return morseVolume;}set{morseVolume=Gain(value);ApplyVolumes();}}
    internal double NoiseVolume {get{return noise.Level;}set{noise.Level=Gain(value);}}
    internal int NoiseKind {get{return noise.Kind;}set{noise.Kind=Math.Max(0,Math.Min(2,value));}}
    internal double SecondaryNoiseVolume {get{return noise.SecondaryLevel;}set{noise.SecondaryLevel=Gain(value);}}
    internal int SecondaryNoiseKind {get{return noise.SecondaryKind;}set{noise.SecondaryKind=Math.Max(0,Math.Min(2,value));}}
    internal bool NoiseSecondaryWhileReading {get{return noise.SecondaryWhileReading;}set{noise.SecondaryWhileReading=value;}}
    internal bool NoisePlaybackOnly {get{return noise.PlaybackOnly;}set{noise.PlaybackOnly=value;}}
    internal void SetReading(bool playing){noise.Playing=playing;}
    internal void ClearPending(){typing.ClearPending();noise.Playing=false;}
    internal void EndDraft(){typing.ClearPending();typing.EndDraft();}
    internal void PlayTyped(string text,double seconds){typing.PlayTyped(text,seconds);}
    internal void PlayFocused(string word,int offset,string element,double seconds){typing.PlayFocused(word,offset,element,seconds);}
    internal void SetSuspended(bool value){noise.Suspended=value;if(value)typing.ClearPending();}
    public void Dispose(){noiseOutput.Dispose();typing.Dispose();}
}

// Independent filtered-random streams: the reading layer adds to the primary.
sealed class NoiseGenerator
{
    readonly object gate=new object();
    readonly DarkNoiseLayer primary=new DarkNoiseLayer(0x937a451bu);
    readonly DarkNoiseLayer secondary=new DarkNoiseLayer(0x34e916c7u);
    double level=.15,secondaryLevel=.15,master=1;
    double primaryAmplitude,secondaryAmplitude;
    int kind,secondaryKind=1;
    bool playing,playbackOnly=true,suspended,secondaryWhileReading;
    internal double Level {get{lock(gate)return level;}set{lock(gate)level=value;}}
    internal double SecondaryLevel {get{lock(gate)return secondaryLevel;}set{lock(gate)secondaryLevel=value;}}
    internal double Master {get{lock(gate)return master;}set{lock(gate)master=value;}}
    internal int Kind {get{lock(gate)return kind;}set{lock(gate)kind=value;}}
    internal int SecondaryKind {get{lock(gate)return secondaryKind;}set{lock(gate)secondaryKind=value;}}
    internal bool SecondaryWhileReading {get{lock(gate)return secondaryWhileReading;}set{lock(gate)secondaryWhileReading=value;}}
    internal bool Playing {get{lock(gate)return playing;}set{lock(gate)playing=value;}}
    internal bool PlaybackOnly {get{lock(gate)return playbackOnly;}set{lock(gate)playbackOnly=value;}}
    internal bool Suspended {get{lock(gate)return suspended;}set{lock(gate)suspended=value;}}
    internal void Fill(short[] samples)
    {
        double primaryTarget,secondaryTarget;int first,second;
        lock(gate){
            primaryTarget=!suspended&&(!playbackOnly||playing)?master*level:0;
            secondaryTarget=!suspended&&playing&&secondaryWhileReading?master*secondaryLevel:0;
            first=kind;second=secondaryKind;
        }
        double rise=1-Math.Exp(-1.0/(MorseOutput.SampleRate*.12));
        double fall=1-Math.Exp(-1.0/(MorseOutput.SampleRate*.40));
        for(int i=0;i<samples.Length;i++){
            primaryAmplitude+=(primaryTarget-primaryAmplitude)*(primaryTarget>primaryAmplitude?rise:fall);
            secondaryAmplitude+=(secondaryTarget-secondaryAmplitude)*(secondaryTarget>secondaryAmplitude?rise:fall);
            if(primaryTarget==0&&primaryAmplitude<1e-6)primaryAmplitude=0;
            if(secondaryTarget==0&&secondaryAmplitude<1e-6)secondaryAmplitude=0;
            double value=.24*(primary.Sample(first)*primaryAmplitude+secondary.Sample(second)*secondaryAmplitude);
            samples[i]=(short)Math.Round(32767*value);
        }
    }
}

sealed class DarkNoiseLayer
{
    static readonly double[] alpha={.035,.015,.006};
    static readonly double[] normalize={Math.Sqrt((2-.035)/.035),Math.Sqrt((2-.015)/.015),Math.Sqrt((2-.006)/.006)};
    static readonly double color=1-Math.Exp(-1.0/(MorseOutput.SampleRate*.08));
    readonly double[] filtered=new double[3],weights={1,0,0};
    uint random;
    internal DarkNoiseLayer(uint seed){random=seed;}
    internal double Sample(int kind)
    {
        random^=random<<13;random^=random>>17;random^=random<<5;
        double white=(random>>8)/8388608.0-1,value=0;
        for(int j=0;j<3;j++){
            filtered[j]+=alpha[j]*(white-filtered[j]);
            weights[j]+=color*((kind==j?1:0)-weights[j]);
            value+=weights[j]*filtered[j]*normalize[j];
        }
        return Math.Tanh(value);
    }
}
