using OpenCvSharp;

namespace FaceRecognition
{
    /// <summary>
    /// Haar Cascade modeli ile görüntüdeki yüzlerin yerini bulur (algılama).
    /// "Bu kim?" sorusunu cevaplamaz; sadece "yüz nerede?" sorusunu cevaplar.
    /// </summary>
    public sealed class FaceDetector : IDisposable
    {
        private readonly CascadeClassifier _cascade;

        public FaceDetector(string cascadePath)
        {
            if (!File.Exists(cascadePath))
            {
                throw new FileNotFoundException("Yüz algılama model dosyası bulunamadı.", cascadePath);
            }

            _cascade = new CascadeClassifier(cascadePath);

            if (_cascade.Empty())
            {
                _cascade.Dispose();
                throw new InvalidOperationException("Yüz algılama modeli yüklenemedi (dosya bozuk olabilir).");
            }
        }

        /// <summary>Gri görüntüdeki tüm yüzlerin dikdörtgenlerini döndürür.</summary>
        public Rect[] Detect(Mat gray)
        {
            return _cascade.DetectMultiScale(
                gray,
                1.1,                               // scaleFactor: her adımda görüntü %10 küçültülerek aranır
                5,                                 // minNeighbors: yüksek değer = daha az yanlış algılama
                HaarDetectionTypes.ScaleImage,
                new OpenCvSharp.Size(80, 80));     // minSize: çok küçük (uzaktaki) yüzleri yok say
        }

        public void Dispose() => _cascade.Dispose();
    }
}
