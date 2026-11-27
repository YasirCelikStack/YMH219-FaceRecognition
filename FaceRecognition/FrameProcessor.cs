using OpenCvSharp;

namespace FaceRecognition
{
    /// <summary>
    /// Bir görüntü karesini baştan sona işler: yüz algılama → tanıma → kutuları çizme.
    /// Hız için iki önlem alır:
    ///  1) Algılamayı küçültülmüş (en fazla DetectionWidth genişliğinde) görüntüde yapar,
    ///     bulunan dikdörtgenleri orijinal boyuta geri ölçekler.
    ///  2) Algılama her karede çalışmak zorunda değildir; çalışmadığı karelerde son sonuçlar yeniden çizilir.
    /// Kamera modunda arka plan iş parçacığında çalışır; bu yüzden kendi Font nesnesini kullanır.
    /// </summary>
    public sealed class FrameProcessor : IDisposable
    {
        /// <summary>Algılama bu genişliğe küçültülmüş görüntüde yapılır (640x480 kamera → 320x240, 4 kat az piksel).</summary>
        private const int DetectionWidth = 320;

        private readonly FaceDetector? _detector;
        private readonly FaceRecognizerService _recognizer;
        private readonly Font _labelFont = new("Segoe UI", 12, FontStyle.Bold);

        private List<FaceResult> _lastFaces = new();

        public bool CanDetect => _detector != null;

        public FrameProcessor(FaceDetector? detector, FaceRecognizerService recognizer)
        {
            _detector = detector;
            _recognizer = recognizer;
        }

        /// <param name="bgr">Renkli kare.</param>
        /// <param name="runDetection">false ise algılama atlanır, önceki karedeki kutular kullanılır.</param>
        public FrameResult Process(Mat bgr, bool runDetection)
        {
            Mat? largestFace = null;

            if (_detector == null)
            {
                _lastFaces = new List<FaceResult>();
            }
            else if (runDetection)
            {
                using Mat gray = ImageUtils.ToGray(bgr);
                Rect[] boxes = DetectScaled(gray);

                var faces = new List<FaceResult>(boxes.Length);
                Rect? largest = null;

                foreach (Rect box in boxes)
                {
                    using Mat face = ImageUtils.CropFace(gray, box);
                    faces.Add(new FaceResult(box, _recognizer.Predict(face)));

                    if (largest == null || Area(box) > Area(largest.Value))
                    {
                        largest = box;
                    }
                }

                // En büyük yüzün kırpıntısı "örnek ekle" için arayüze gönderilir
                if (largest != null)
                {
                    largestFace = ImageUtils.CropFace(gray, largest.Value);
                }

                _lastFaces = faces;
            }

            Bitmap bitmap = ImageUtils.ToBitmap(bgr);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                foreach (FaceResult face in _lastFaces)
                {
                    DrawFaceBox(g, face);
                }
            }

            return new FrameResult(bitmap, _lastFaces, largestFace, runDetection);
        }

        /// <summary>Görüntüyü küçültüp algılar, sonuçları orijinal boyuta geri ölçekler.</summary>
        private Rect[] DetectScaled(Mat gray)
        {
            double scale = Math.Min(1.0, DetectionWidth / (double)gray.Width);

            if (scale >= 1.0)
            {
                return _detector!.Detect(gray, minFaceSize: 30); // zaten küçük bir resim
            }

            using var small = new Mat();
            Cv2.Resize(gray, small, new OpenCvSharp.Size(0, 0), scale, scale, InterpolationFlags.Area);

            Rect[] smallBoxes = _detector!.Detect(small, minFaceSize: 40);

            return smallBoxes
                .Select(r => new Rect(
                    (int)(r.X / scale),
                    (int)(r.Y / scale),
                    (int)(r.Width / scale),
                    (int)(r.Height / scale)))
                .ToArray();
        }

        private void DrawFaceBox(Graphics g, FaceResult face)
        {
            Rect box = face.Box;
            RecognitionResult result = face.Result;

            System.Drawing.Color color = !_recognizer.IsTrained ? System.Drawing.Color.DeepSkyBlue
                                        : result.IsKnown ? System.Drawing.Color.LimeGreen
                                        : System.Drawing.Color.OrangeRed;

            using var pen = new Pen(color, 3);
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);

            string text = _recognizer.IsTrained
                ? $"{result.DisplayName} (%{result.ConfidencePercent})"
                : "Yüz";

            SizeF textSize = g.MeasureString(text, _labelFont);
            float textY = Math.Max(0, box.Y - textSize.Height);

            using var background = new SolidBrush(color);
            g.FillRectangle(background, box.X, textY, textSize.Width, textSize.Height);
            g.DrawString(text, _labelFont, Brushes.Black, box.X, textY);
        }

        private static int Area(Rect r) => r.Width * r.Height;

        public void Dispose() => _labelFont.Dispose();
    }

    /// <summary>Tek bir yüzün konumu ve tanıma sonucu.</summary>
    public record FaceResult(Rect Box, RecognitionResult Result);

    /// <summary>
    /// İşlenmiş bir karenin sonucu. Image ve LargestFace'in sahipliği arayüze geçer;
    /// kare gösterilmeden atılırsa Dispose çağrılmalıdır.
    /// </summary>
    public sealed class FrameResult : IDisposable
    {
        public Bitmap Image { get; }
        public IReadOnlyList<FaceResult> Faces { get; }
        public Mat? LargestFace { get; }
        public bool DetectionRan { get; }

        public FrameResult(Bitmap image, IReadOnlyList<FaceResult> faces, Mat? largestFace, bool detectionRan)
        {
            Image = image;
            Faces = faces;
            LargestFace = largestFace;
            DetectionRan = detectionRan;
        }

        public void Dispose()
        {
            Image.Dispose();
            LargestFace?.Dispose();
        }
    }
}
