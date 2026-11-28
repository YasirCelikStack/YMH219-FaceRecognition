using OpenCvSharp;

namespace FaceRecognition
{
    /// <summary>
    /// Yerel yüz örneklerini klasörlerde tutar:
    ///   Dataset/
    ///     Yasir/  20261124_223015_123.png ...
    ///     Ahmet/  ...
    /// Klasör adı = etiket (kişinin adı). Kişi ekleme, yeniden adlandırma ve silme işlemleri buradadır.
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
            string folder = LabelFolder(label);
            Directory.CreateDirectory(folder);

            string filePath = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            ImageUtils.SaveImage(filePath, face);
            return filePath;
        }

        /// <summary>Her etiket için kaç örnek olduğunu döndürür (ada göre sıralı).</summary>
        public SortedDictionary<string, int> GetSampleCounts()
        {
            var counts = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
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

        /// <summary>
        /// Kişinin adını değiştirir. Yeni adla zaten bir kişi varsa örnekler onunla birleştirilir.
        /// </summary>
        public void RenameLabel(string oldLabel, string newLabel)
        {
            string oldFolder = LabelFolder(oldLabel);
            string newFolder = LabelFolder(newLabel);

            if (!Directory.Exists(oldFolder))
            {
                throw new DirectoryNotFoundException($"'{oldLabel}' adlı kişi bulunamadı.");
            }
            if (string.Equals(oldFolder, newFolder, StringComparison.Ordinal)) return; // aynı ad

            if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            {
                // Sadece büyük/küçük harf değişti (yasir → Yasir): Windows'ta doğrudan taşınamaz, geçici adla iki adımda
                string temp = Path.Combine(RootPath, "_tmp_" + Guid.NewGuid().ToString("N"));
                Directory.Move(oldFolder, temp);
                Directory.Move(temp, newFolder);
                return;
            }

            if (!Directory.Exists(newFolder))
            {
                Directory.Move(oldFolder, newFolder);
                return;
            }

            // Hedef kişi zaten var: dosyaları birleştir (aynı ad varsa yeni ad ver)
            foreach (string file in Directory.GetFiles(oldFolder))
            {
                string target = Path.Combine(newFolder, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    target = Path.Combine(newFolder, $"{Path.GetFileNameWithoutExtension(file)}_{Guid.NewGuid():N}{Path.GetExtension(file)}");
                }
                File.Move(file, target);
            }
            Directory.Delete(oldFolder, recursive: true);
        }

        /// <summary>Kişiyi ve tüm örneklerini siler. Silinen örnek sayısını döndürür.</summary>
        public int DeleteLabel(string label)
        {
            string folder = LabelFolder(label);
            if (!Directory.Exists(folder)) return 0;

            int count = Directory.GetFiles(folder, "*.png").Length;
            Directory.Delete(folder, recursive: true);
            return count;
        }

        /// <summary>
        /// Veri kümesinde son değişikliğin zamanı (örnek eklendi, silindi, ad değişti).
        /// Kaydedilmiş model bundan eskiyse yeniden eğitilmesi gerekir.
        /// </summary>
        public DateTime LastChangeTime()
        {
            DateTime latest = Directory.GetLastWriteTime(RootPath);
            foreach (string folder in Directory.GetDirectories(RootPath))
            {
                DateTime folderTime = Directory.GetLastWriteTime(folder);
                if (folderTime > latest) latest = folderTime;

                foreach (string file in Directory.GetFiles(folder, "*.png"))
                {
                    DateTime fileTime = File.GetLastWriteTime(file);
                    if (fileTime > latest) latest = fileTime;
                }
            }
            return latest;
        }

        /// <summary>Etiketin klasör yolu. Geçersiz bir ad verilirse ArgumentException fırlatır.</summary>
        public string LabelFolder(string label)
        {
            string safeLabel = SanitizeLabel(label);
            if (safeLabel.Length == 0)
            {
                throw new ArgumentException("Geçerli bir etiket (isim) girin.");
            }
            return Path.Combine(RootPath, safeLabel);
        }

        /// <summary>Etiketten klasör adında kullanılamayan karakterleri (\ / : * ? " &lt; &gt; |) temizler.</summary>
        private static string SanitizeLabel(string label)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(label.Trim().Where(c => !invalid.Contains(c)).ToArray()).Trim('.', ' ');
        }
    }
}
