using OpenCvSharp;

namespace FaceRecognition
{
    /// <summary>
    /// Yerel yüz örneklerini klasörlerde tutar:
    ///   Dataset/
    ///     Yasir/  20261124_223015_123.png ...
    ///     Ahmet/  ...
    /// Klasör adı = etiket (kişinin adı).
    /// </summary>
    public class FaceDataset
    {
        public string RootPath { get; }

        public FaceDataset(string rootPath)
        {
            RootPath = rootPath;
            Directory.CreateDirectory(RootPath);
        }

        /// <summary>Kırpılmış gri yüz görüntüsünü ilgili etiketin klasörüne kaydeder.</summary>
        public string AddSample(string label, Mat face)
        {
            string safeLabel = SanitizeLabel(label);
            if (safeLabel.Length == 0)
            {
                throw new ArgumentException("Geçerli bir etiket (isim) girin.");
            }

            string folder = Path.Combine(RootPath, safeLabel);
            Directory.CreateDirectory(folder);

            string filePath = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            ImageUtils.SaveImage(filePath, face);
            return filePath;
        }

        /// <summary>Her etiket için kaç örnek olduğunu döndürür.</summary>
        public Dictionary<string, int> GetSampleCounts()
        {
            var counts = new Dictionary<string, int>();
            foreach (string folder in Directory.GetDirectories(RootPath))
            {
                counts[Path.GetFileName(folder)] = Directory.GetFiles(folder, "*.png").Length;
            }
            return counts;
        }

        /// <summary>Tüm örnekleri (etiket, dosya yolu) çiftleri olarak döndürür.</summary>
        public List<(string Label, string FilePath)> GetAllSamples()
        {
            var samples = new List<(string, string)>();
            foreach (string folder in Directory.GetDirectories(RootPath))
            {
                string label = Path.GetFileName(folder);
                foreach (string file in Directory.GetFiles(folder, "*.png"))
                {
                    samples.Add((label, file));
                }
            }
            return samples;
        }

        /// <summary>Etiketten klasör adında kullanılamayan karakterleri (\ / : * ? " < > |) temizler.</summary>
        private static string SanitizeLabel(string label)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(label.Trim().Where(c => !invalid.Contains(c)).ToArray());
        }
    }
}
