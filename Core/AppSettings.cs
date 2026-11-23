using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Xml;

namespace Raskladka
{
    public enum MotionMode { System, Full, Minimal }

    public sealed class AppSettings
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public HotkeyBinding Hotkey { get; set; }
        public ConversionMode Direction { get; set; }
        public bool RecentInput { get; set; }
        public bool SwitchLayout { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool Notify { get; set; }
        public MotionMode Motion { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }

        public AppSettings()
        {
            Left = double.NaN;
            Top = double.NaN;
        }

        public static string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raskladka", "settings.xml");
            }
        }

        public static AppSettings Load() { return Load(SettingsPath); }

        internal static AppSettings Load(string path)
        {
            var settings = new AppSettings();
            try
            {
                var readerOptions = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16384 };
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, readerOptions)) document.Load(reader);
                var root = document.DocumentElement;
                if (root == null || root.Name != "settings") return settings;

                settings.Direction = ReadEnum(root, "direction", ConversionMode.Auto);
                settings.Motion = ReadEnum(root, "motion", MotionMode.System);
                settings.RecentInput = ReadFlag(root, "recentInput");
                settings.SwitchLayout = ReadFlag(root, "switchLayout");
                settings.AlwaysOnTop = ReadFlag(root, "alwaysOnTop");
                settings.Notify = ReadFlag(root, "notify");
                settings.Left = ReadCoordinate(root, "left");
                settings.Top = ReadCoordinate(root, "top");

                int mods, key;
                if (int.TryParse(root.GetAttribute("modifiers"), out mods) && int.TryParse(root.GetAttribute("virtualKey"), out key))
                {
                    var binding = new HotkeyBinding(mods, key);
                    if (binding.IsValid) settings.Hotkey = binding;
                }
            }
            catch (IOException) { }
            catch (XmlException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            return settings;
        }

        private static T ReadEnum<T>(XmlElement root, string name, T fallback) where T : struct
        {
            T value;
            return Enum.TryParse(root.GetAttribute(name), out value) && Enum.IsDefined(typeof(T), value) ? value : fallback;
        }

        private static bool ReadFlag(XmlElement root, string name)
        {
            bool value;
            return bool.TryParse(root.GetAttribute(name), out value) && value;
        }

        private static double ReadCoordinate(XmlElement root, string name)
        {
            double value;
            return double.TryParse(root.GetAttribute(name), NumberStyles.Float, Invariant, out value) && !double.IsInfinity(value) ? value : double.NaN;
        }

        public bool Save() { return Save(SettingsPath); }

        internal bool Save(string path)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("settings");
                    writer.WriteAttributeString("direction", Direction.ToString());
                    writer.WriteAttributeString("motion", Motion.ToString());
                    writer.WriteAttributeString("recentInput", XmlConvert.ToString(RecentInput));
                    writer.WriteAttributeString("switchLayout", XmlConvert.ToString(SwitchLayout));
                    writer.WriteAttributeString("alwaysOnTop", XmlConvert.ToString(AlwaysOnTop));
                    writer.WriteAttributeString("notify", XmlConvert.ToString(Notify));
                    if (Hotkey != null && Hotkey.IsValid)
                    {
                        writer.WriteAttributeString("modifiers", Hotkey.Modifiers.ToString(Invariant));
                        writer.WriteAttributeString("virtualKey", Hotkey.VirtualKey.ToString(Invariant));
                    }
                    if (!double.IsNaN(Left) && !double.IsInfinity(Left)) writer.WriteAttributeString("left", Left.ToString("R", Invariant));
                    if (!double.IsNaN(Top) && !double.IsInfinity(Top)) writer.WriteAttributeString("top", Top.ToString("R", Invariant));
                    writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { }
            }
        }
    }
}
