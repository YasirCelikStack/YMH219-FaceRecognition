using OpenCvSharp;

namespace FaceRecognition
{
    /// <summary>
    /// Kamerayı açar, kare okur ve kapatır. Form, kameranın ayrıntılarını bilmek zorunda kalmaz.
    /// </summary>
    public sealed class CameraService : IDisposable
    {
        private VideoCapture? _capture;

        public bool IsRunning => _capture != null;

        /// <summary>
        /// Kamerayı arka planda açar (açılış 1-2 sn sürebilir; arayüz donmasın diye Task.Run).
        /// Kamera yoksa veya başka uygulama kullanıyorsa false döner.
        /// </summary>
        public async Task<bool> StartAsync(int cameraIndex = 0)
        {
            Stop();

            VideoCapture capture = await Task.Run(() => new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW));

            if (!capture.IsOpened())
            {
                capture.Dispose();
                return false;
            }

            // Bazı kameralar varsayılan olarak 1280x720 verir; 640x480 hem yeterli hem çok daha hızlı.
            capture.Set(VideoCaptureProperties.FrameWidth, 640);
            capture.Set(VideoCaptureProperties.FrameHeight, 480);

            _capture = capture;
            return true;
        }

        /// <summary>Kameradan bir kare okur. Başarılıysa true döner.</summary>
        public bool ReadFrame(Mat frame)
        {
            if (_capture == null) return false;
            return _capture.Read(frame) && !frame.Empty();
        }

        public void Stop()
        {
            if (_capture == null) return;

            _capture.Release();
            _capture.Dispose();
            _capture = null;
        }

        public void Dispose() => Stop();
    }
}
