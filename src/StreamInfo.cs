using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace UxPlayMirror
{
    /// What the phone is playing over AirPlay audio (title, artist, cover art, position).
    class NowPlaying
    {
        public string Title, Artist, Album, Genre;
        public int Position, Duration;          // seconds
        public DateTime ProgressAt;             // when Position was last reported
        public DateTime UpdatedAt;
        public int CoverVersion;                // bumps whenever uxplay rewrites the cover file
        public bool Active;

        public static readonly string CoverFile = Path.Combine(Paths.LocalDir, @"nowplaying\cover.jpg");

        public bool HasTrack { get { return !string.IsNullOrEmpty(Title) || !string.IsNullOrEmpty(Artist); } }

        /// Playing if the phone reported a position in the last few seconds.
        public bool IsPlaying { get { return (DateTime.Now - ProgressAt).TotalSeconds < 3.5; } }

        public double LivePosition
        {
            get
            {
                double p = Position + (IsPlaying ? (DateTime.Now - ProgressAt).TotalSeconds : 0);
                return Duration > 0 ? Math.Min(p, Duration) : p;
            }
        }

        public bool HasCover
        {
            get { try { var f = new FileInfo(CoverFile); return f.Exists && f.Length > 2048; } catch { return false; } }
        }

        public void Clear()
        {
            Title = Artist = Album = Genre = null;
            Position = Duration = 0;
            Active = false;
            ProgressAt = DateTime.MinValue;
        }

        static readonly Regex progress = new Regex(@"audio progress \(min:sec\):\s*(\d+):(\d+); remaining:\s*(\d+):(\d+); track length (\d+):(\d+)");
        static readonly Regex field = new Regex(@"^(Title|Artist|Album|Genre): (.*)$");

        public enum Parse { None, Progress, Metadata, MetadataStart, Cover }

        /// Feeds one uxplay output line. Returns what kind of line it was.
        public Parse Feed(string l)
        {
            var m = progress.Match(l);
            if (m.Success)
            {
                Position = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
                Duration = int.Parse(m.Groups[5].Value) * 60 + int.Parse(m.Groups[6].Value);
                ProgressAt = DateTime.Now;
                Active = true;
                return Parse.Progress;
            }
            if (l.StartsWith("=====") && l.Contains("Audio Metadata"))
            {
                Title = Artist = Album = Genre = null;
                Active = true;
                return Parse.MetadataStart;
            }
            var f = field.Match(l);
            if (f.Success)
            {
                string v = f.Groups[2].Value.Trim();
                switch (f.Groups[1].Value)
                {
                    case "Title": Title = v; break;
                    case "Artist": Artist = v; break;
                    case "Album": Album = v; break;
                    default: Genre = v; break;
                }
                UpdatedAt = DateTime.Now;
                Active = true;
                return Parse.Metadata;
            }
            if (l.StartsWith("coverart size"))
            {
                CoverVersion++;
                UpdatedAt = DateTime.Now;
                Active = true;
                return Parse.Cover;
            }
            return Parse.None;
        }
    }

    /// Reads the once-a-second video statistics plist the phone sends when uxplay runs with -FPSdata.
    static class FpsReport
    {
        static bool sampleSaved;

        /// Returns frames per second, or -1 if the report has no frame-rate field.
        public static double Parse(string xml)
        {
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var doc = new XmlDocument();
                doc.XmlResolver = null;
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
                using (var r = XmlReader.Create(new StringReader(xml), settings)) doc.Load(r);
                var root = doc.SelectSingleNode("/plist/*");
                if (root != null) Walk(root, "", values);
            }
            catch (Exception ex) { Log.Write("fps plist: " + ex.Message); return -1; }

            if (!sampleSaved)
            {
                sampleSaved = true;
                try { File.WriteAllText(Path.Combine(Paths.LogDir, "fpsdata-sample.xml"), xml); } catch { }
                Log.Write("fps plist keys: " + string.Join(", ", values.Keys));
            }

            // iOS reports several *FPS counters, most of them dropped frames (sinkOverflowDropFPS, encoderDropFPS…).
            // Frames actually sent per second is sentFramesAvg; the others are fallbacks for older iOS versions.
            foreach (var key in new[] { "sentFramesAvg", "submitSurfaceFPS", "beforeEncoderFPS", "fps" })
                foreach (var kv in values)
                    if (LastPart(kv.Key).Equals(key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            foreach (var kv in values)
            {
                string k = LastPart(kv.Key);
                if (k.IndexOf("drop", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (k.IndexOf("fps", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(k, "frame.?rate", RegexOptions.IgnoreCase)) return kv.Value;
            }
            return -1;
        }

        static string LastPart(string path) { int i = path.LastIndexOf('/'); return i < 0 ? path : path.Substring(i + 1); }

        static void Walk(XmlNode node, string path, Dictionary<string, double> values)
        {
            if (node.Name == "dict")
            {
                string key = null;
                foreach (XmlNode c in node.ChildNodes)
                {
                    if (c.NodeType != XmlNodeType.Element) continue;
                    if (c.Name == "key") { key = c.InnerText; continue; }
                    if (key != null) Walk(c, path.Length > 0 ? path + "/" + key : key, values);
                    key = null;
                }
            }
            else if (node.Name == "array")
            {
                int i = 0;
                foreach (XmlNode c in node.ChildNodes)
                    if (c.NodeType == XmlNodeType.Element) Walk(c, path + "/" + (i++), values);
            }
            else if (node.Name == "real" || node.Name == "integer")
            {
                double d;
                if (double.TryParse(node.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !values.ContainsKey(path))
                    values[path] = d;
            }
        }
    }
}
