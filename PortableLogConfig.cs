using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace NutstoreAutoPatch
{
    public static class PortableLogConfig
    {
        private const string Feature = "logconfig";
        private const string PortablePrefix = @"..\UserData\logs\";
        private static readonly string[] Prefixes = {
            @"${APPDATA}\Nutstore\logs\",
            @"${ALLUSERSPROFILE}\Nutstore\logs\",
            @"${ALLUSERSPROFILE}\Application Data\Nutstore\logs\",
            PortablePrefix
        };

        private static XmlDocument Load(byte[] bytes)
        {
            try
            {
                var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (var stream = new MemoryStream(bytes))
                using (var reader = XmlReader.Create(stream, settings)) document.Load(reader);
                return document;
            }
            catch (XmlException) { throw new InvalidOperationException("组件配置不是受支持的安全 XML，未修改文件。"); }
        }

        private static string Target(string value)
        {
            string normalized = value.Replace('/', '\\');
            string prefix = Prefixes.FirstOrDefault(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix == null) return null;
            string leaf = normalized.Substring(prefix.Length);
            if (leaf.Length == 0 || leaf == "." || leaf == ".." || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                leaf.Contains("${") || leaf.Contains("%"))
                throw new InvalidOperationException("日志文件名含不支持的路径或变量，未修改配置。");
            return PortablePrefix + leaf;
        }

        private static XmlAttribute[] Values(XmlDocument document)
        {
            return document.SelectNodes("/configuration/log4net/appender/file/@value")
                .Cast<XmlAttribute>().Where(a => Target(a.Value) != null).ToArray();
        }

        public static string[] FindConfigs(string binDirectory)
        {
            return Directory.GetFiles(binDirectory, "*.exe.config", SearchOption.TopDirectoryOnly)
                .Where(path => Values(Load(File.ReadAllBytes(path))).Length != 0)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static void Validate(string path)
        {
            if (Values(Load(File.ReadAllBytes(path))).Length == 0)
                throw new InvalidOperationException("此组件配置没有可识别的坚果云日志路径。");
        }

        public static bool IsPortable(string path)
        {
            var values = Values(Load(File.ReadAllBytes(path)));
            return values.Length != 0 && values.All(value => value.Value == Target(value.Value));
        }

        public static bool Apply(string path, bool createBackup)
        {
            string originalHash = Patcher.Hash(path);
            byte[] original = File.ReadAllBytes(path);
            var document = Load(original);
            var values = Values(document);
            if (values.Length == 0) throw new InvalidOperationException("此组件配置没有可识别的坚果云日志路径。");
            if (values.All(value => value.Value == Target(value.Value))) return false;
            foreach (var value in values) value.Value = Target(value.Value);
            var declaration = document.FirstChild as XmlDeclaration;
            string encodingName = declaration == null || string.IsNullOrEmpty(declaration.Encoding) ? "utf-8" : declaration.Encoding;
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(encodingName); }
            catch (ArgumentException) { throw new InvalidOperationException("组件配置使用不支持的字符编码。"); }
            if (encoding.CodePage == Encoding.UTF8.CodePage)
                encoding = new UTF8Encoding(original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf);
            byte[] replacement;
            using (var output = new MemoryStream())
            {
                var settings = new XmlWriterSettings { Encoding = encoding, Indent = false, OmitXmlDeclaration = declaration == null, NewLineHandling = NewLineHandling.None };
                using (var writer = XmlWriter.Create(output, settings)) document.Save(writer);
                replacement = output.ToArray();
            }
            return FileChanges.Apply(path, replacement, createBackup, Feature, originalHash);
        }

        public static bool CanRestore(string path) { return FileChanges.CanRestore(path, Feature); }
        public static void Restore(string path) { FileChanges.Restore(path, Feature); }
    }
}
