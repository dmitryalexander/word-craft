using System;
using System.Collections.Generic;

// Stateful PBD cloth, ported from the selected demo. No WPF/editor dependencies.
sealed class WordCraftClothSimulation
{
    internal const int Columns=64,Rows=28,Count=(Columns+1)*(Rows+1);
    internal readonly double[] Positions=new double[Count*3],Rest=new double[Count*3];
    readonly double[] previous=new double[Count*3],normal=new double[Count*3],tethers=new double[Count*4];
    readonly int[] pins={0,Columns,Rows*(Columns+1),Count-1};
    readonly bool[] pinned=new bool[Count];
    struct Link {internal int A,B;internal double Length,Stiffness;}
    readonly Link[] links;
    internal double Time {get;private set;}
    internal WordCraftClothSimulation(double height)
    {
        for(int j=0;j<=Rows;j++)for(int i=0;i<=Columns;i++){int q=(j*(Columns+1)+i)*3;Rest[q]=3.4*i/Columns-1.7;Rest[q+1]=height*j/Rows-height/2;}
        foreach(int pin in pins)pinned[pin]=true;
        var list=new List<Link>();
        for(int j=0;j<=Rows;j++)for(int i=0;i<=Columns;i++){
            int a=j*(Columns+1)+i;
            if(i<Columns)Add(list,a,a+1,1);if(j<Rows)Add(list,a,a+Columns+1,1);
            if(i<Columns&&j<Rows){Add(list,a,a+Columns+2,.65);Add(list,a+1,a+Columns+1,.65);}
            if(i<Columns-1)Add(list,a,a+2,.08);if(j<Rows-1)Add(list,a,a+2*(Columns+1),.08);
        }
        links=list.ToArray();
        for(int i=0;i<Count;i++)for(int k=0;k<4;k++){int a=i*3,b=pins[k]*3;tethers[i*4+k]=Length(Rest[a]-Rest[b],Rest[a+1]-Rest[b+1],0);}
        Reset();
    }
    void Add(List<Link> list,int a,int b,double stiffness){list.Add(new Link {A=a,B=b,Length=Length(Rest[a*3]-Rest[b*3],Rest[a*3+1]-Rest[b*3+1],0),Stiffness=stiffness});}
    static double Length(double x,double y,double z){return Math.Sqrt(x*x+y*y+z*z);}
    static double Clamp(double x,double max){return Math.Max(-max,Math.Min(max,x));}
    internal void Reset()
    {
        Array.Copy(Rest,Positions,Rest.Length);
        for(int j=0;j<=Rows;j++)for(int i=0;i<=Columns;i++){int q=(j*(Columns+1)+i)*3;if(!pinned[q/3])Positions[q+2]=.004*Math.Sin(Math.PI*i/Columns)*Math.Sin(Math.PI*j/Rows);}
        Array.Copy(Positions,previous,Positions.Length);Time=0;
    }
    internal static void NormalAt(double[] source,int index,double[] target)
    {
        int i=index%(Columns+1),j=index/(Columns+1),l=(index-(i>0?1:0))*3,r=(index+(i<Columns?1:0))*3,b=(index-(j>0?Columns+1:0))*3,t=(index+(j<Rows?Columns+1:0))*3;
        double ax=source[r]-source[l],ay=source[r+1]-source[l+1],az=source[r+2]-source[l+2],bx=source[t]-source[b],by=source[t+1]-source[b+1],bz=source[t+2]-source[b+2];
        double x=ay*bz-az*by,y=az*bx-ax*bz,z=ax*by-ay*bx,length=Length(x,y,z);if(length<1e-12)length=1;int q=index*3;target[q]=x/length;target[q+1]=y/length;target[q+2]=z/length;
    }
    static double Hash(int x,int y)
    {unchecked{int n=x*374761393^y*668265263;n=(n^(int)((uint)n>>13))*1274126177;return (uint)(n^(int)((uint)n>>16))/2147483648.0-1;}}
    static double Noise(double x,double y)
    {int i=(int)Math.Floor(x),j=(int)Math.Floor(y);double fx=x-i,fy=y-j,u=fx*fx*(3-2*fx),v=fy*fy*(3-2*fy);return (Hash(i,j)*(1-u)+Hash(i+1,j)*u)*(1-v)+(Hash(i,j+1)*(1-u)+Hash(i+1,j+1)*u)*v;}
    internal void Step(double wind,double turbulence,double tautness,double gravity)
    {
        const double dt=1.0/120;double stretch=1.05-.048*tautness;Time+=dt;var p=Positions;
        for(int i=0;i<Count;i++)NormalAt(p,i,normal);
        for(int i=0;i<Count;i++){
            if(pinned[i])continue;int q=i*3;double x=p[q],y=p[q+1],z=p[q+2],vx=(x-previous[q])/dt,vy=(y-previous[q+1])/dt,vz=(z-previous[q+2])/dt;
            double gx=Rest[q]*2-4*wind*Time,gy=Rest[q+1]*2,gust=.6*Noise(gx,gy)+.27*Noise(gx*2.03+7.1,gy*2.03)+.13*Noise(gx*4.13,gy*4.13+19.4);
            double rx=4*wind*(1+.5*turbulence*gust)-vx,ry=.5*wind*turbulence*Noise(gx+21.7,gy-9.4)-vy,rz=wind*(1.2+1.1*turbulence*gust)-vz;
            double vn=rx*normal[q]+ry*normal[q+1]+rz*normal[q+2],force=Clamp(2*vn*Math.Abs(vn),80);
            previous[q]=x;previous[q+1]=y;previous[q+2]=z;
            p[q]=x+Clamp(vx*dt*.995+(force*normal[q]+.12*rx)*dt*dt,.05);
            p[q+1]=y+Clamp(vy*dt*.995+(force*normal[q+1]+.12*ry-9.81*gravity)*dt*dt,.05);
            p[q+2]=z+Clamp(vz*dt*.995+(force*normal[q+2]+.12*rz)*dt*dt,.05);
        }
        for(int iteration=0;iteration<8;iteration++){
            for(int n=0;n<links.Length;n++){
                Link link=links[iteration%2==1?links.Length-1-n:n];double wa=pinned[link.A]?0:1,wb=pinned[link.B]?0:1,weight=wa+wb;if(weight==0)continue;
                int a=link.A*3,b=link.B*3;double dx=p[b]-p[a],dy=p[b+1]-p[a+1],dz=p[b+2]-p[a+2],length=Length(dx,dy,dz);if(length<1e-9)continue;
                double correction=(length-link.Length*stretch)/length*link.Stiffness/weight;
                p[a]+=dx*correction*wa;p[a+1]+=dy*correction*wa;p[a+2]+=dz*correction*wa;p[b]-=dx*correction*wb;p[b+1]-=dy*correction*wb;p[b+2]-=dz*correction*wb;
            }
            for(int i=0;i<Count;i++){
                if(pinned[i])continue;int q=i*3;
                for(int k=0;k<4;k++){
                    int pin=pins[k]*3;double dx=p[q]-Rest[pin],dy=p[q+1]-Rest[pin+1],dz=p[q+2],length=Length(dx,dy,dz),limit=tethers[i*4+k]*stretch;
                    if(length>limit){double ratio=limit/length;p[q]=Rest[pin]+dx*ratio;p[q+1]=Rest[pin+1]+dy*ratio;p[q+2]=dz*ratio;}
                }
            }
        }
    }
    internal double PinError {get{double error=0;foreach(int pin in pins)for(int c=0;c<3;c++)error=Math.Max(error,Math.Abs(Positions[pin*3+c]-Rest[pin*3+c]));return error;}}
    internal double MaxDepth {get{double value=0;for(int i=2;i<Positions.Length;i+=3)value=Math.Max(value,Math.Abs(Positions[i]));return value;}}
}
