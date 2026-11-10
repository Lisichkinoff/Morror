using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace Morror
{
    public static class MonitorResolution
    {
        public static void Validate(int width,int height,int fps)
        {
            if(width<16 || height<16 || width>8192 || height>8192 || width%2!=0 || height%2!=0 || (fps!=30 && fps!=60))
                throw new ArgumentException("Недопустимый размер монитора или частота кадров.");
        }
        // The Android receiver is landscape. Use the effective Android size (including wm overrides).
        // HD/FHD are budgets for the LONG edge, never a forced 16:9 aspect ratio.
        public static Size ForDevice(int deviceWidth,int deviceHeight,int quality)
        {
            if(deviceWidth<16 || deviceHeight<16 || deviceWidth>8192 || deviceHeight>8192 || quality<0 || quality>2)
                throw new ArgumentException("Не удалось определить корректное разрешение экрана телефона.");
            int longEdge=Math.Max(deviceWidth,deviceHeight),shortEdge=Math.Min(deviceWidth,deviceHeight);
            int maximum=quality==0?1280:quality==1?1920:longEdge;
            double scale=Math.Min(1,(double)maximum/longEdge);
            int width=(int)(longEdge*scale)/2*2;
            int height=(int)Math.Round(shortEdge*scale/2)*2;
            Validate(width,height,60);return new Size(width,height);
        }
        // Preserve all existing modes, monitor counts, GPU options and comments.
        public static bool AddMode(XmlDocument xml,int width,int height,int fps)
        {
            Validate(width,height,fps);
            if(xml.DocumentElement==null || xml.DocumentElement.Name!="vdd_settings")throw new IOException("Неизвестный формат настроек видеодрайвера.");
            var resolutions=xml.DocumentElement.SelectSingleNode("resolutions");
            if(resolutions==null){resolutions=xml.CreateElement("resolutions");xml.DocumentElement.AppendChild(resolutions);}
            XmlNode matching=null;
            foreach(XmlNode mode in resolutions.SelectNodes("resolution"))
            {
                int w,h;
                if(int.TryParse(mode.SelectSingleNode("width")==null?null:mode.SelectSingleNode("width").InnerText,out w) &&
                    int.TryParse(mode.SelectSingleNode("height")==null?null:mode.SelectSingleNode("height").InnerText,out h) && w==width && h==height){matching=mode;break;}
            }
            if(matching==null)
            {
                matching=xml.CreateElement("resolution");resolutions.AppendChild(matching);
                var w=xml.CreateElement("width");w.InnerText=width.ToString();matching.AppendChild(w);
                var h=xml.CreateElement("height");h.InnerText=height.ToString();matching.AppendChild(h);
            }
            else if(matching.SelectNodes("refresh_rate").Cast<XmlNode>().Any(n=>n.InnerText==fps.ToString()) ||
                xml.DocumentElement.SelectNodes("global/g_refresh_rate").Cast<XmlNode>().Any(n=>n.InnerText==fps.ToString()))return false;
            var rate=xml.CreateElement("refresh_rate");rate.InnerText=fps.ToString();matching.AppendChild(rate);return true;
        }
        public static bool UpdateConfiguration(string path,int width,int height,int fps)
        {
            Validate(width,height,fps);
            if(new FileInfo(path).Length>1024*1024)throw new IOException("Файл настроек видеодрайвера слишком большой.");
            var xml=new XmlDocument{PreserveWhitespace=true,XmlResolver=null};
            using(var reader=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))xml.Load(reader);
            if(!AddMode(xml,width,height,fps))return false;
            string temporary=path+".morror-"+Guid.NewGuid().ToString("N")+".tmp";
            string backup=path+".morror-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".bak";
            try
            {
                using(var writer=XmlWriter.Create(temporary,new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true}))xml.Save(writer);
                File.Replace(temporary,path,backup);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            return true;
        }
    }
}
