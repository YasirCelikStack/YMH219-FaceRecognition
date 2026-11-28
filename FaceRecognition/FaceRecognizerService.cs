using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.Face;

namespace FaceRecognition
{
    /// <summary>
    /// LBPH (Local Binary Patterns Histograms) yöntemiyle yüz tanıma.
    /// Train: örnek yüzlerden her kişi için doku histogramları çıkarır.
    /// Predict: yeni yüzün histogramını örneklerle karşılaştırır, en yakın kişiyi ve mesafeyi verir.
    /// Eğitilen model dosyaya kaydedilip açılışta tekrar yüklenebilir (yeniden eğitmeye gerek kalmaz).
    /// </summary>
    public sealed class FaceRecognizerService : IDisposable
    {
        /// <summary>Varsayılan eşik: mesafe bu değerden büyükse yüz "Bilinmiyor" sayılır.</summary>
        public const double DefaultThreshold = 80;

        private LBPHFaceRecognizer? _recognizer;
        private Dictionary<int, string> _labelNames = new(); // sayı -> isim (LBPH etiketleri sayı ister)
        private double _threshold = DefaultThreshold;

        // Kamera görüntüsü arka plan iş parçacığında işlenir; eğitim ve ayar değişimi ise arayüzden.
        // Aynı anda hem Predict hem model/eşik değişimi olmasın diye kilit (lock) kullanılır.
        private readonly object _sync = new();

        public bool IsTrained
        {
            get { lock (_sync) return _recognizer != null; }
        }

        public int LabelCount
        {
            get { lock (_sync) return _labelNames.Count; }
        }

        /// <summary>
        /// Tanıma eşiği. Küçük değer = daha seçici (yabancıyı tanıdık sanma azalır, ama tanıdığı da bazen "Bilinmiyor" der).
        /// Büyük değer = daha hoşgörülü.
        /// </summary>
        public double Threshold
        {
            get { lock (_sync) return _threshold; }
            set { lock (_sync) _threshold = Math.Clamp(value, 1, 500); }
        }

        /// <summary>Veri kümesindeki tüm örneklerle modeli eğitir.</summary>
        public TrainResult Train(FaceDataset dataset)
        {
            var samples = dataset.GetAllSamples();
            if (samples.Count == 0)
            {
                throw new InvalidOperationException("Eğitim için hiç örnek yok. Önce yüz örneği ekleyin.");
            }

            var images = new List<Mat>();
            var labels = new List<int>();
            var labelIds = new Dictionary<string, int>();
            int skipped = 0;

            try
            {
                foreach (var (label, filePath) in samples)
                {
                    Mat image;
                    try
                    {
                        image = ImageUtils.LoadImage(filePath, ImreadModes.Grayscale);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        skipped++; // bozuk/okunamayan dosyayı atla
                        continue;
                    }

                    if (image.Width != ImageUtils.FaceSize || image.Height != ImageUtils.FaceSize)
                    {
                        Cv2.Resize(image, image, new OpenCvSharp.Size(ImageUtils.FaceSize, ImageUtils.FaceSize));
                    }

                    if (!labelIds.TryGetValue(label, out int id))
                    {
                        id = labelIds.Count;
                        labelIds[label] = id;
                    }

                    images.Add(image);
                    labels.Add(id);
                }

                if (images.Count == 0)
                {
                    throw new InvalidOperationException("Örnek dosyaları okunamadı.");
                }

                var recognizer = LBPHFaceRecognizer.Create();
                recognizer.Train(images, labels);

                // Eski modeli bırak, yenisini kullan (kilit içinde: o an Predict çalışıyor olabilir)
                ReplaceModel(recognizer, labelIds.ToDictionary(p => p.Value, p => p.Key));

                return new TrainResult(images.Count, labelIds.Count, skipped);
            }
            finally
            {
                foreach (var image in images) image.Dispose();
            }
        }

        /// <summary>Kırpılmış gri yüzün kime ait olduğunu tahmin eder.</summary>
        public RecognitionResult Predict(Mat face)
        {
            int labelId;
            double distance;
            string name;
            double threshold;

            lock (_sync)
            {
                if (_recognizer == null) return RecognitionResult.NotTrained;

                _recognizer.Predict(face, out labelId, out distance);
                name = _labelNames.TryGetValue(labelId, out var n) ? n : "?";
                threshold = _threshold;
            }

            bool isKnown = distance <= threshold;

            return new RecognitionResult(isKnown ? name : "Bilinmiyor", name, distance, isKnown);
        }

        // ---------- Modeli dosyaya kaydetme / dosyadan yükleme ----------

        /// <summary>
        /// Eğitilmiş modeli kaydeder: LBPH histogramları .yml dosyasına (OpenCV biçimi),
        /// sayı → isim eşleşmesi .json dosyasına. Model yoksa hiçbir şey yapmaz.
        /// </summary>
        public void Save(string modelPath, string labelsPath)
        {
            lock (_sync)
            {
                if (_recognizer == null) return;

                Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
                _recognizer.Write(modelPath);
                File.WriteAllText(labelsPath, JsonSerializer.Serialize(_labelNames));
            }
        }

        /// <summary>Kaydedilmiş modeli yükler. Dosyalar yoksa veya bozuksa false döner (uygulama çökmez).</summary>
        public bool TryLoad(string modelPath, string labelsPath)
        {
            if (!File.Exists(modelPath) || !File.Exists(labelsPath)) return false;

            LBPHFaceRecognizer? recognizer = null;
            try
            {
                var labels = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(labelsPath));
                if (labels == null || labels.Count == 0) return false;

                recognizer = LBPHFaceRecognizer.Create();
                recognizer.Read(modelPath);

                ReplaceModel(recognizer, labels);
                return true;
            }
            catch (Exception ex) when (ex is OpenCVException or IOException or UnauthorizedAccessException or JsonException)
            {
                recognizer?.Dispose();
                return false;
            }
        }

        /// <summary>Modeli bellekten siler (ör. tüm kişiler silindiğinde).</summary>
        public void Reset()
        {
            lock (_sync)
            {
                _recognizer?.Dispose();
                _recognizer = null;
                _labelNames = new Dictionary<int, string>();
            }
        }

        private void ReplaceModel(LBPHFaceRecognizer recognizer, Dictionary<int, string> labelNames)
        {
            lock (_sync)
            {
                _recognizer?.Dispose();
                _recognizer = recognizer;
                _labelNames = labelNames;
            }
        }

        public void Dispose() => Reset();
    }

    /// <summary>Eğitim özeti: kaç örnek, kaç kişi, kaç dosya atlandı.</summary>
    public record TrainResult(int SampleCount, int LabelCount, int SkippedCount);
}
