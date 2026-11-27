using OpenCvSharp;
using OpenCvSharp.Face;

namespace FaceRecognition
{
    /// <summary>
    /// LBPH (Local Binary Patterns Histograms) yöntemiyle yüz tanıma.
    /// Train: örnek yüzlerden her kişi için doku histogramları çıkarır.
    /// Predict: yeni yüzün histogramını örneklerle karşılaştırır, en yakın kişiyi ve mesafeyi verir.
    /// </summary>
    public sealed class FaceRecognizerService : IDisposable
    {
        /// <summary>Mesafe bu değerden büyükse yüz "Bilinmiyor" sayılır. Ortama göre ayarlanabilir.</summary>
        public const double UnknownThreshold = 80;

        private LBPHFaceRecognizer? _recognizer;
        private readonly Dictionary<int, string> _labelNames = new(); // sayı -> isim (LBPH etiketleri sayı ister)

        // Kamera görüntüsü arka plan iş parçacığında işlenir; eğitim ise arayüzden başlatılır.
        // Aynı anda hem Predict hem model değişimi olmasın diye kilit (lock) kullanılır.
        private readonly object _sync = new();

        public bool IsTrained => _recognizer != null;
        public int LabelCount => _labelNames.Count;

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
                lock (_sync)
                {
                    _recognizer?.Dispose();
                    _recognizer = recognizer;

                    _labelNames.Clear();
                    foreach (var pair in labelIds)
                    {
                        _labelNames[pair.Value] = pair.Key;
                    }
                }

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

            lock (_sync)
            {
                if (_recognizer == null) return RecognitionResult.NotTrained;

                _recognizer.Predict(face, out labelId, out distance);
                name = _labelNames.TryGetValue(labelId, out var n) ? n : "?";
            }

            bool isKnown = distance <= UnknownThreshold;

            return new RecognitionResult(isKnown ? name : "Bilinmiyor", name, distance, isKnown);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _recognizer?.Dispose();
                _recognizer = null;
            }
        }
    }

    /// <summary>Eğitim özeti: kaç örnek, kaç kişi, kaç dosya atlandı.</summary>
    public record TrainResult(int SampleCount, int LabelCount, int SkippedCount);
}
