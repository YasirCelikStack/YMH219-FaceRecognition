using System.Diagnostics;
using OpenCvSharp;

namespace FaceRecognition
{
    public partial class Form1 : Form
    {
        /// <summary>Algılama kaç karede bir çalışsın (2 = her iki karede bir; aradaki karede son kutular kullanılır).</summary>
        private const int DetectEveryNFrames = 2;

        /// <summary>"Otomatik örnek topla" kaç örnek alsın.</summary>
        private const int BurstSampleCount = 10;

        // ---------- Servisler (iş mantığı) ----------
        private readonly CameraService _camera = new();
        private readonly FaceRecognizerService _recognizer = new();
        private readonly FaceDataset _dataset = new(Path.Combine(AppContext.BaseDirectory, "Dataset"));
        private readonly FaceDetector? _detector; // model dosyası yoksa null kalır
        private readonly FrameProcessor _processor;

        // ---------- Arka plan kamera döngüsü ----------
        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private int _uiBusy; // 1 = arayüz hâlâ önceki kareyi gösteriyor (Interlocked ile kullanılır)

        // ---------- Arayüz durumu ----------
        private Mat? _lastFaceCrop;        // en son algılanan en büyük yüz (örnek eklerken kullanılır)
        private int _burstRemaining;       // otomatik örnek toplamada kalan sayı
        private string _burstLabel = "";
        private readonly Stopwatch _burstTimer = new();
        private readonly Stopwatch _fpsTimer = new();
        private int _fpsFrames;

        // ---------- Arayüz kontrolleri ----------
        private readonly PictureBox _pictureBox = new()
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = System.Drawing.Color.Black
        };

        private readonly Button _btnStartCamera = new() { Text = "Kamera Başlat", Width = 230, Height = 32 };
        private readonly Button _btnStopCamera = new() { Text = "Kamera Durdur", Width = 230, Height = 32, Enabled = false };
        private readonly Button _btnSelectImage = new() { Text = "Görüntü Seç...", Width = 230, Height = 32 };
        private readonly TextBox _txtLabel = new() { Width = 230, PlaceholderText = "Etiket (ör. Yasir)" };
        private readonly Button _btnAddSample = new() { Text = "Yüz Örneği Ekle (1)", Width = 230, Height = 32 };
        private readonly Button _btnBurst = new() { Text = $"Otomatik Örnek Topla ({BurstSampleCount})", Width = 230, Height = 32 };
        private readonly Button _btnTrain = new() { Text = "Modeli Eğit", Width = 230, Height = 32 };
        private readonly Label _lblDataset = new() { Width = 230, Height = 90 };
        private readonly Label _lblResult = new()
        {
            Width = 230,
            Height = 110,
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };

        private readonly StatusStrip _statusStrip = new();
        private readonly ToolStripStatusLabel _lblStatus = new();

        public Form1()
        {
            InitializeComponent();
            BuildUi();

            _detector = LoadDetector();
            _processor = new FrameProcessor(_detector, _recognizer);

            _btnStartCamera.Click += BtnStartCamera_Click;
            _btnStopCamera.Click += async (s, e) => await StopCameraAsync();
            _btnSelectImage.Click += BtnSelectImage_Click;
            _btnAddSample.Click += BtnAddSample_Click;
            _btnBurst.Click += BtnBurst_Click;
            _btnTrain.Click += BtnTrain_Click;
            FormClosing += Form1_FormClosing;

            TryAutoTrain();
            UpdateDatasetInfo();
        }

        private void BuildUi()
        {
            Text = "Yüz Algılama ve Tanıma - YMH219";
            Size = new System.Drawing.Size(1000, 640);
            MinimumSize = new System.Drawing.Size(800, 520);
            StartPosition = FormStartPosition.CenterScreen;

            var side = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 255,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10)
            };

            side.Controls.Add(MakeHeader("Kaynak"));
            side.Controls.Add(_btnStartCamera);
            side.Controls.Add(_btnStopCamera);
            side.Controls.Add(_btnSelectImage);

            side.Controls.Add(MakeHeader("Eğitim"));
            side.Controls.Add(_txtLabel);
            side.Controls.Add(_btnAddSample);
            side.Controls.Add(_btnBurst);
            side.Controls.Add(_btnTrain);
            side.Controls.Add(_lblDataset);

            side.Controls.Add(MakeHeader("Sonuç"));
            side.Controls.Add(_lblResult);

            _statusStrip.Items.Add(_lblStatus);

            // Dock sırası: Fill önce, kenarlar sonra
            Controls.Add(_pictureBox);
            Controls.Add(side);
            Controls.Add(_statusStrip);

            SetStatus("Hazır. Kamerayı başlatın veya bir görüntü seçin.", System.Drawing.Color.Gray);
        }

        private static Label MakeHeader(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 4)
        };

        // ---------- Başlangıç ----------

        private static FaceDetector? LoadDetector()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Models", "haarcascade_frontalface_default.xml");
            try
            {
                return new FaceDetector(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                MessageBox.Show($"{ex.Message}\n\nBeklenen konum:\n{path}\n\nYüz algılama devre dışı.",
                    "Model dosyası hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        /// <summary>Daha önce eklenmiş örnek varsa açılışta modeli otomatik eğitir.</summary>
        private void TryAutoTrain()
        {
            if (_dataset.GetAllSamples().Count == 0) return;
            try
            {
                _recognizer.Train(_dataset);
            }
            catch (Exception ex)
            {
                SetStatus($"Model otomatik eğitilemedi: {ex.Message}", System.Drawing.Color.DarkOrange);
            }
        }

        // ---------- Kamera ----------

        private async void BtnStartCamera_Click(object? sender, EventArgs e)
        {
            _btnStartCamera.Enabled = false;
            SetStatus("Kamera açılıyor...", System.Drawing.Color.DarkOrange);

            bool opened;
            try
            {
                opened = await _camera.StartAsync(0);
            }
            catch (Exception)
            {
                opened = false;
            }

            if (!opened)
            {
                _btnStartCamera.Enabled = true;
                SetStatus("Kamera açılamadı.", System.Drawing.Color.Red);
                MessageBox.Show("Kamera bulunamadı veya başka bir uygulama tarafından kullanılıyor.",
                    "Kamera hatası", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnStopCamera.Enabled = true;
            _fpsFrames = 0;
            _fpsTimer.Restart();
            SetStatus("● Kamera açık", System.Drawing.Color.Green);

            // Kare okuma + işleme arka planda; arayüz donmaz.
            _loopCts = new CancellationTokenSource();
            CancellationToken token = _loopCts.Token;
            _loopTask = Task.Run(() => CaptureLoop(token));
        }

        /// <summary>
        /// Arka plan döngüsü: kare oku → işle → arayüze gönder.
        /// Arayüz önceki kareyi henüz göstermediyse yeni kare atlanır (kuyruk birikmez, gecikme olmaz).
        /// </summary>
        private void CaptureLoop(CancellationToken token)
        {
            using var frame = new Mat();
            int frameIndex = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_camera.ReadFrame(frame))
                    {
                        Thread.Sleep(10); // görüntü gelmedi, kısa bekle
                        continue;
                    }

                    // Arayüz meşgulse bu kareyi atla
                    if (Interlocked.CompareExchange(ref _uiBusy, 1, 0) != 0) continue;

                    bool detect = frameIndex++ % DetectEveryNFrames == 0;
                    FrameResult result = _processor.Process(frame, detect);

                    if (!PostToUi(() => ShowFrame(result)))
                    {
                        result.Dispose(); // form kapanıyor
                        return;
                    }
                }
                catch (OpenCVException ex)
                {
                    PostToUi(() => OnCameraError(ex.Message));
                    return;
                }
            }
        }

        /// <summary>Arka plan iş parçacığından arayüz iş parçacığına iş gönderir.</summary>
        private bool PostToUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return false;
            try
            {
                BeginInvoke(action);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private async Task StopCameraAsync()
        {
            if (_loopCts != null)
            {
                _loopCts.Cancel();
                if (_loopTask != null)
                {
                    try { await _loopTask; } catch (OperationCanceledException) { }
                }
                _loopCts.Dispose();
                _loopCts = null;
                _loopTask = null;
            }

            _camera.Stop();
            Interlocked.Exchange(ref _uiBusy, 0);
            _burstRemaining = 0;

            _btnStartCamera.Enabled = true;
            _btnStopCamera.Enabled = false;
            SetStatus("Kamera durduruldu.", System.Drawing.Color.Gray);
        }

        private async void OnCameraError(string message)
        {
            await StopCameraAsync();
            MessageBox.Show($"Görüntü işlenirken hata oluştu:\n{message}", "Hata",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ---------- Görüntü dosyası ----------

        private async void BtnSelectImage_Click(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Resim dosyaları|*.jpg;*.jpeg;*.png;*.bmp|Tüm dosyalar|*.*"
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;

            if (_camera.IsRunning) await StopCameraAsync();

            try
            {
                using Mat image = ImageUtils.LoadImage(dialog.FileName, ImreadModes.Color);
                ShowFrame(_processor.Process(image, runDetection: true));
                SetStatus($"Görüntü: {Path.GetFileName(dialog.FileName)}", System.Drawing.Color.Black);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OpenCVException)
            {
                MessageBox.Show($"Görüntü açılamadı:\n{ex.Message}", "Hata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------- İşlenmiş kareyi gösterme ----------

        private void ShowFrame(FrameResult result)
        {
            try
            {
                if (IsDisposed)
                {
                    result.Dispose();
                    return;
                }

                // Eski görüntüyü bellekten sil, yenisini göster
                Image? old = _pictureBox.Image;
                _pictureBox.Image = result.Image;
                old?.Dispose();

                // Son yüz kırpıntısını güncelle (algılama bu karede çalıştıysa)
                if (result.DetectionRan)
                {
                    _lastFaceCrop?.Dispose();
                    _lastFaceCrop = result.LargestFace;
                    HandleBurst();
                }

                ShowResultText(result.Faces);
                UpdateFps();
            }
            finally
            {
                Interlocked.Exchange(ref _uiBusy, 0); // arka plan döngüsü yeni kare gönderebilir
            }
        }

        private void ShowResultText(IReadOnlyList<FaceResult> faces)
        {
            if (!_processor.CanDetect)
            {
                _lblResult.Text = "Yüz algılama kapalı\n(model dosyası yok)";
                return;
            }

            if (faces.Count == 0)
            {
                _lblResult.Text = "Yüz bulunamadı";
                return;
            }

            // En büyük (kameraya en yakın) yüzün sonucunu göster
            RecognitionResult main = faces.OrderByDescending(f => f.Box.Width * f.Box.Height).First().Result;

            if (!_recognizer.IsTrained)
            {
                _lblResult.Text = $"Algılanan yüz: {faces.Count}\nTanıma için önce örnek ekleyip modeli eğitin.";
                return;
            }

            _lblResult.Text =
                $"Algılanan yüz: {faces.Count}\n" +
                $"Tahmin: {main.DisplayName}\n" +
                $"En yakın: {main.NearestLabel}\n" +
                $"Mesafe: {main.Distance:0.0} (eşik {FaceRecognizerService.UnknownThreshold})\n" +
                $"Güven: %{main.ConfidencePercent}";
        }

        private void UpdateFps()
        {
            if (!_camera.IsRunning) return;

            _fpsFrames++;
            if (_fpsTimer.ElapsedMilliseconds >= 1000)
            {
                string burst = _burstRemaining > 0
                    ? $"   |   Örnek toplanıyor: {BurstSampleCount - _burstRemaining}/{BurstSampleCount} (başını hafifçe çevir)"
                    : "";
                SetStatus($"● Kamera açık — {_fpsFrames} FPS{burst}", System.Drawing.Color.Green);
                _fpsFrames = 0;
                _fpsTimer.Restart();
            }
        }

        // ---------- Eğitim ----------

        private void BtnAddSample_Click(object? sender, EventArgs e)
        {
            string label = _txtLabel.Text.Trim();
            if (!ValidateCanAddSample(label)) return;

            if (_lastFaceCrop == null)
            {
                MessageBox.Show("Görüntüde yüz bulunamadı.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (SaveSample(label, _lastFaceCrop))
            {
                SetStatus($"'{label}' için örnek eklendi. (Modeli yeniden eğitmeyi unutmayın)", System.Drawing.Color.Green);
            }
        }

        /// <summary>Kameradan yaklaşık 0.3 sn arayla otomatik olarak birkaç örnek toplar.</summary>
        private void BtnBurst_Click(object? sender, EventArgs e)
        {
            string label = _txtLabel.Text.Trim();
            if (!ValidateCanAddSample(label)) return;

            if (!_camera.IsRunning)
            {
                MessageBox.Show("Otomatik toplama için kamerayı başlatın.", "Bilgi",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _burstLabel = label;
            _burstRemaining = BurstSampleCount;
            _burstTimer.Restart();
        }

        /// <summary>Otomatik toplama açıksa ve yeterli süre geçtiyse son yüzü örnek olarak kaydeder.</summary>
        private void HandleBurst()
        {
            if (_burstRemaining <= 0 || _lastFaceCrop == null) return;
            if (_burstTimer.ElapsedMilliseconds < 300) return;

            if (!SaveSample(_burstLabel, _lastFaceCrop))
            {
                _burstRemaining = 0; // kayıt hatası: toplamayı durdur
                return;
            }

            _burstRemaining--;
            _burstTimer.Restart();

            if (_burstRemaining == 0)
            {
                SetStatus($"'{_burstLabel}' için {BurstSampleCount} örnek eklendi. Şimdi 'Modeli Eğit'e basın.",
                    System.Drawing.Color.Green);
            }
        }

        private bool ValidateCanAddSample(string label)
        {
            if (label.Length == 0)
            {
                MessageBox.Show("Önce bir etiket (isim) yazın.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (!_processor.CanDetect)
            {
                MessageBox.Show("Yüz algılama modeli yüklenemediği için örnek eklenemez.", "Bilgi",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        private bool SaveSample(string label, Mat face)
        {
            try
            {
                _dataset.AddSample(label, face);
                UpdateDatasetInfo();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OpenCVException)
            {
                MessageBox.Show($"Örnek kaydedilemedi:\n{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void BtnTrain_Click(object? sender, EventArgs e)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                TrainResult result = _recognizer.Train(_dataset);

                string message = $"Model eğitildi.\nÖrnek: {result.SampleCount}\nKişi (etiket): {result.LabelCount}";
                if (result.SkippedCount > 0) message += $"\nOkunamayan dosya: {result.SkippedCount}";
                if (result.LabelCount < 2) message += "\n\nNot: Anlamlı bir tanıma için en az 2 farklı etiket ekleyin.";

                MessageBox.Show(message, "Eğitim", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Model eğitildi.", System.Drawing.Color.Green);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OpenCVException)
            {
                MessageBox.Show(ex.Message, "Eğitim yapılamadı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void UpdateDatasetInfo()
        {
            var counts = _dataset.GetSampleCounts();
            if (counts.Count == 0)
            {
                _lblDataset.Text = "Henüz örnek yok.\nHer kişi için 10-20 örnek önerilir.";
                return;
            }

            _lblDataset.Text = "Örnekler:\n" + string.Join("\n", counts.Select(c => $"• {c.Key}: {c.Value}"));
        }

        // ---------- Yardımcılar ----------

        private void SetStatus(string text, System.Drawing.Color color)
        {
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Önce arka plan döngüsünü durdur, sonra kaynakları bırak
            _loopCts?.Cancel();
            try { _loopTask?.Wait(1000); } catch (AggregateException) { }

            _camera.Dispose();
            _processor.Dispose();
            _detector?.Dispose();
            _recognizer.Dispose();
            _lastFaceCrop?.Dispose();
            _pictureBox.Image?.Dispose();
        }
    }
}
