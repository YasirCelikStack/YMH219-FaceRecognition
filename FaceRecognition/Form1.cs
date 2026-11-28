using System.Diagnostics;
using OpenCvSharp;

namespace FaceRecognition
{
    /// <summary>
    /// Ana pencere: solda kamera/görüntü, sağda kartlar hâlinde kontroller
    /// (Kaynak, Sonuç, Eğitim, Kişiler, Ayarlar). Kamera kareleri arka planda işlenir.
    /// </summary>
    public partial class Form1 : Form
    {
        /// <summary>Algılama kaç karede bir çalışsın (2 = her iki karede bir; aradaki karede son kutular kullanılır).</summary>
        private const int DetectEveryNFrames = 2;

        /// <summary>"Otomatik örnek topla" kaç örnek alsın.</summary>
        private const int BurstSampleCount = 10;

        private const int CardWidth = 300;
        private const int ContentWidth = CardWidth - 28;

        // ---------- Servisler (iş mantığı) ----------
        private readonly CameraService _camera = new();
        private readonly FaceRecognizerService _recognizer = new();
        private readonly FaceDataset _dataset = new(Path.Combine(AppContext.BaseDirectory, "Dataset"));
        private readonly FaceCaptureWriter _captureWriter = new();
        private readonly FaceSettings _settings = FaceSettings.Load();
        private readonly FaceDetector? _detector; // model dosyası yoksa null kalır
        private readonly FrameProcessor _processor;

        private static readonly string ModelFolder = Path.Combine(AppContext.BaseDirectory, "Model");
        private static readonly string ModelPath = Path.Combine(ModelFolder, "lbph_model.yml");
        private static readonly string LabelsPath = Path.Combine(ModelFolder, "etiketler.json");

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
            BackColor = System.Drawing.Color.FromArgb(24, 26, 32)
        };
        private readonly Label _lblPlaceholder = new()
        {
            Dock = DockStyle.Fill,
            Text = "📷\n\nKamerayı başlatın veya bir görüntü seçin",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = System.Drawing.Color.FromArgb(150, 156, 170),
            BackColor = System.Drawing.Color.FromArgb(24, 26, 32),
            Font = new Font("Segoe UI", 13)
        };

        // Kaynak
        private readonly Button _btnStartCamera = Btn("▶  Kamera Başlat", accent: true);
        private readonly Button _btnStopCamera = Btn("■  Kamera Durdur");
        private readonly Button _btnSelectImage = Btn("🖼  Görüntü Seç...");

        // Sonuç
        private readonly Label _lblResultName = new() { Width = ContentWidth, Height = 36, Font = new Font("Segoe UI", 17, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Text = "—" };
        private readonly ConfidenceBar _confidence = new() { Width = ContentWidth, Height = 12, Margin = new Padding(0, 4, 0, 6) };
        private readonly Label _lblResultDetail = new() { Width = ContentWidth, Height = 58, ForeColor = Palette.Muted };

        // Eğitim
        private readonly TextBox _txtLabel = new() { Width = ContentWidth, PlaceholderText = "Kişinin adı (ör. Yasir)", Font = new Font("Segoe UI", 10.5f), Margin = new Padding(0, 0, 0, 6) };
        private readonly Button _btnAddSample = Btn("Yüz Örneği Ekle (1)");
        private readonly Button _btnBurst = Btn($"Otomatik Örnek Topla ({BurstSampleCount})");
        private readonly Button _btnTrain = Btn("🧠  Modeli Eğit ve Kaydet", accent: true);

        // Kişiler
        private readonly ListView _lvPeople = new()
        {
            Width = ContentWidth,
            Height = 120,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            BorderStyle = BorderStyle.FixedSingle
        };
        private readonly Button _btnRename = SmallBtn("Yeniden Adlandır");
        private readonly Button _btnDeletePerson = SmallBtn("Sil", danger: true);
        private readonly Button _btnOpenDataset = SmallBtn("Klasör");

        // Ayarlar
        private readonly TrackBar _trkThreshold = new() { Minimum = 30, Maximum = 150, TickFrequency = 10, Width = ContentWidth, AutoSize = false, Height = 32 };
        private readonly Label _lblThreshold = new() { Width = ContentWidth, Height = 22, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) };
        private readonly Label _lblThresholdHint = new() { Width = ContentWidth, Height = 34, ForeColor = Palette.Muted, Font = new Font("Segoe UI", 8.5f), Text = "Küçük = daha seçici (yabancıyı tanıdık sanmaz)\nBüyük = daha hoşgörülü" };
        private readonly CheckBox _chkPrivacy = new() { Text = "Gizlilik modu: yüzleri bulanıklaştır", Width = ContentWidth, Height = 26 };
        private readonly CheckBox _chkCapture = new() { Text = "Algılanan yüzleri klasöre kaydet", Width = ContentWidth - 70, Height = 26 };
        private readonly Button _btnOpenCaptures = SmallBtn("Klasör");

        private readonly StatusStrip _statusStrip = new() { SizingGrip = false, BackColor = Palette.Card };
        private readonly ToolStripStatusLabel _lblStatus = new();

        public Form1()
        {
            InitializeComponent();
            BuildUi();

            _detector = LoadDetector();
            _processor = new FrameProcessor(_detector, _recognizer);

            // Kaydedilmiş ayarları uygula
            _recognizer.Threshold = _settings.Threshold;
            _trkThreshold.Value = (int)Math.Clamp(_settings.Threshold, _trkThreshold.Minimum, _trkThreshold.Maximum);
            _chkPrivacy.Checked = _settings.PrivacyMode;
            _processor.PrivacyMode = _settings.PrivacyMode;
            UpdateThresholdLabel();

            _btnStartCamera.Click += BtnStartCamera_Click;
            _btnStopCamera.Click += async (s, e) => await StopCameraAsync();
            _btnSelectImage.Click += BtnSelectImage_Click;
            _btnAddSample.Click += BtnAddSample_Click;
            _btnBurst.Click += BtnBurst_Click;
            _btnTrain.Click += BtnTrain_Click;
            _btnRename.Click += BtnRename_Click;
            _btnDeletePerson.Click += BtnDeletePerson_Click;
            _btnOpenDataset.Click += (s, e) => OpenFolder(_dataset.RootPath);
            _btnOpenCaptures.Click += (s, e) => OpenFolder(_captureWriter.RootPath);
            _lvPeople.SelectedIndexChanged += (s, e) => UpdatePeopleButtons();
            _lvPeople.DoubleClick += (s, e) =>
            {
                if (_lvPeople.SelectedItems.Count > 0) _txtLabel.Text = _lvPeople.SelectedItems[0].Text; // seçilen kişiye örnek eklemek kolay olsun
            };
            _trkThreshold.ValueChanged += TrkThreshold_ValueChanged;
            _chkPrivacy.CheckedChanged += ChkPrivacy_CheckedChanged;
            FormClosing += Form1_FormClosing;

            _btnStopCamera.Enabled = false;
            LoadOrTrainModel();
            UpdateDatasetInfo();
            ShowResult(Array.Empty<FaceResult>());
        }

        // =====================================================================
        // Arayüz kurulumu
        // =====================================================================

        private void BuildUi()
        {
            Text = "Yüz Algılama ve Tanıma - YMH219";
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Palette.Background;
            ForeColor = Palette.Text;
            Size = new System.Drawing.Size(1180, 760);
            MinimumSize = new System.Drawing.Size(960, 620);
            StartPosition = FormStartPosition.CenterScreen;

            _lvPeople.Columns.Add("Kişi", 170);
            _lvPeople.Columns.Add("Örnek", 70, HorizontalAlignment.Right);

            var peopleButtons = Row(_btnRename, _btnDeletePerson, _btnOpenDataset);
            var captureRow = Row(_chkCapture, _btnOpenCaptures);
            _btnOpenCaptures.Width = 64;

            var side = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = CardWidth + 34,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10, 10, 10, 10),
                BackColor = Palette.Background
            };
            side.Controls.Add(Card("Kaynak", _btnStartCamera, _btnStopCamera, _btnSelectImage));
            side.Controls.Add(Card("Sonuç", _lblResultName, _confidence, _lblResultDetail));
            side.Controls.Add(Card("Eğitim", _txtLabel, _btnAddSample, _btnBurst, _btnTrain));
            side.Controls.Add(Card("Kişiler (çift tık: ada örnek ekle)", _lvPeople, peopleButtons));
            side.Controls.Add(Card("Ayarlar", _lblThreshold, _trkThreshold, _lblThresholdHint, _chkPrivacy, captureRow));

            // Görüntü alanı: koyu çerçeve içinde
            var viewer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 4, 12) };
            var viewerInner = new Panel { Dock = DockStyle.Fill, BackColor = _pictureBox.BackColor };
            viewerInner.Controls.Add(_pictureBox);
            viewerInner.Controls.Add(_lblPlaceholder);
            _lblPlaceholder.BringToFront(); // görüntü gelene kadar bilgi yazısı üstte dursun
            viewer.Controls.Add(viewerInner);

            _statusStrip.Items.Add(_lblStatus);

            // Dock sırası: Fill önce, kenarlar sonra
            Controls.Add(viewer);
            Controls.Add(side);
            Controls.Add(_statusStrip);

            SetStatus("Hazır. Kamerayı başlatın veya bir görüntü seçin.", Palette.Muted);
        }

        /// <summary>Başlıklı beyaz kart; içindeki kontroller alt alta dizilir ve kart yüksekliği içeriğe göre ayarlanır.</summary>
        private static Panel Card(string title, params Control[] items)
        {
            var body = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new System.Drawing.Point(14, 38),
                BackColor = Palette.Card,
                Margin = new Padding(0)
            };
            foreach (Control item in items)
            {
                if (item is Button && item.Margin == new Padding(3)) item.Margin = new Padding(0, 0, 0, 6);
                body.Controls.Add(item);
            }

            var card = new Panel { Width = CardWidth, BackColor = Palette.Card, Margin = new Padding(0, 0, 0, 10) };
            card.Controls.Add(body);
            card.Controls.Add(new Label
            {
                Text = title,
                Location = new System.Drawing.Point(14, 10),
                AutoSize = true,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
            });
            // Kart yüksekliği = içerideki kontrollerin yükseklikleri + kenar boşlukları + başlık
            card.Height = items.Sum(i => i.Height + i.Margin.Vertical) + 52;
            card.Paint += (s, e) =>
            {
                using var pen = new Pen(Palette.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            return card;
        }

        private static FlowLayoutPanel Row(params Control[] items)
        {
            var row = new FlowLayoutPanel { Width = ContentWidth, Height = 34, WrapContents = false, Margin = new Padding(0, 6, 0, 0), BackColor = Palette.Card };
            foreach (Control item in items)
            {
                item.Margin = new Padding(0, 0, 6, 0);
                row.Controls.Add(item);
            }
            return row;
        }

        private static Button Btn(string text, bool accent = false)
        {
            var button = new Button { Text = text, Width = ContentWidth, Height = 34, Margin = new Padding(0, 0, 0, 6) };
            Style(button, accent ? Palette.Accent : (System.Drawing.Color?)null);
            return button;
        }

        private static Button SmallBtn(string text, bool danger = false)
        {
            var button = new Button { Text = text, Height = 30, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(60, 30) };
            Style(button, danger ? Palette.Danger : (System.Drawing.Color?)null);
            return button;
        }

        private static void Style(Button button, System.Drawing.Color? fill)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.Cursor = Cursors.Hand;
            button.BackColor = fill ?? Palette.Card;
            button.ForeColor = fill == null ? Palette.Text : System.Drawing.Color.White;
            button.FlatAppearance.BorderColor = fill ?? Palette.Border;
            button.FlatAppearance.MouseOverBackColor = fill == null
                ? System.Drawing.Color.FromArgb(240, 243, 250)
                : ControlPaint.Light(fill.Value, 0.15f);
            button.Font = new Font("Segoe UI", 9.75f, fill == null ? FontStyle.Regular : FontStyle.Bold);
        }

        // =====================================================================
        // Başlangıç: algılama modeli ve tanıma modeli
        // =====================================================================

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

        /// <summary>
        /// Açılışta: kaydedilmiş model veri kümesinden yeniyse onu yükle (hızlı),
        /// değilse örneklerle yeniden eğit ve kaydet.
        /// </summary>
        private void LoadOrTrainModel()
        {
            if (_dataset.GetAllSamples().Count == 0) return;

            bool modelIsFresh = File.Exists(ModelPath) && File.GetLastWriteTime(ModelPath) >= _dataset.LastChangeTime();
            if (modelIsFresh && _recognizer.TryLoad(ModelPath, LabelsPath))
            {
                SetStatus($"Kaydedilmiş model yüklendi ({_recognizer.LabelCount} kişi).", Palette.Known);
                return;
            }

            try
            {
                _recognizer.Train(_dataset);
                SaveModel();
                SetStatus($"Model örneklerden eğitildi ({_recognizer.LabelCount} kişi).", Palette.Known);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OpenCVException or IOException)
            {
                SetStatus($"Model otomatik eğitilemedi: {ex.Message}", Palette.Unknown);
            }
        }

        /// <summary>Modeli dosyaya kaydeder. Kaydedilemezse sadece uyarır (bir sonraki açılışta yeniden eğitilir).</summary>
        private bool SaveModel()
        {
            try
            {
                _recognizer.Save(ModelPath, LabelsPath);
                return true;
            }
            catch (Exception ex) when (ex is OpenCVException or IOException or UnauthorizedAccessException)
            {
                SetStatus($"Model dosyaya kaydedilemedi: {ex.Message}", Palette.Unknown);
                return false;
            }
        }

        /// <summary>Kişi silindi/yeniden adlandırıldı: modeli güncel örneklerle yeniden kur.</summary>
        private void RetrainAfterDatasetChange()
        {
            if (_dataset.GetAllSamples().Count == 0)
            {
                _recognizer.Reset();
                TryDelete(ModelPath);
                TryDelete(LabelsPath);
                SetStatus("Hiç örnek kalmadı; model sıfırlandı.", Palette.Muted);
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                TrainResult result = _recognizer.Train(_dataset);
                SaveModel();
                SetStatus($"Model güncellendi: {result.LabelCount} kişi, {result.SampleCount} örnek.", Palette.Known);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OpenCVException)
            {
                SetStatus($"Model güncellenemedi: {ex.Message}", Palette.Unknown);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // =====================================================================
        // Kamera
        // =====================================================================

        private async void BtnStartCamera_Click(object? sender, EventArgs e)
        {
            _btnStartCamera.Enabled = false;
            SetStatus("Kamera açılıyor...", Palette.Unknown);

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
                SetStatus("Kamera açılamadı.", Palette.Danger);
                MessageBox.Show("Kamera bulunamadı veya başka bir uygulama tarafından kullanılıyor.",
                    "Kamera hatası", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnStopCamera.Enabled = true;
            _lblPlaceholder.Visible = false;
            _fpsFrames = 0;
            _fpsTimer.Restart();
            SetStatus("● Kamera açık", Palette.Known);

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
            SetStatus("Kamera durduruldu.", Palette.Muted);
        }

        private async void OnCameraError(string message)
        {
            await StopCameraAsync();
            MessageBox.Show($"Görüntü işlenirken hata oluştu:\n{message}", "Hata",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // =====================================================================
        // Görüntü dosyası
        // =====================================================================

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
                _lblPlaceholder.Visible = false;
                ShowFrame(_processor.Process(image, runDetection: true));
                SetStatus($"Görüntü: {Path.GetFileName(dialog.FileName)}", Palette.Text);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OpenCVException)
            {
                MessageBox.Show($"Görüntü açılamadı:\n{ex.Message}", "Hata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =====================================================================
        // İşlenmiş kareyi gösterme
        // =====================================================================

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
                    CaptureIfEnabled(result);
                    _lastFaceCrop?.Dispose();
                    _lastFaceCrop = result.LargestFace;
                    HandleBurst();
                }

                ShowResult(result.Faces);
                UpdateFps();
            }
            finally
            {
                Interlocked.Exchange(ref _uiBusy, 0); // arka plan döngüsü yeni kare gönderebilir
            }
        }

        /// <summary>"Algılanan yüzleri kaydet" açıksa en büyük yüzü tahmin edilen kişinin klasörüne yazar.</summary>
        private void CaptureIfEnabled(FrameResult result)
        {
            if (!_chkCapture.Checked || result.LargestFace == null || result.Faces.Count == 0) return;

            RecognitionResult main = result.Faces.OrderByDescending(f => f.Box.Width * f.Box.Height).First().Result;
            string label = main.IsKnown ? main.DisplayName : "Bilinmiyor";

            try
            {
                _captureWriter.TrySave(result.LargestFace, label);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
            {
                _chkCapture.Checked = false;
                SetStatus($"Yüz kaydedilemedi, kaydetme kapatıldı: {ex.Message}", Palette.Danger);
            }
        }

        /// <summary>Sonuç kartı: en büyük (kameraya en yakın) yüzün adı, güven çubuğu ve ayrıntılar.</summary>
        private void ShowResult(IReadOnlyList<FaceResult> faces)
        {
            if (!_processor.CanDetect)
            {
                SetResult("Algılama kapalı", Palette.Muted, 0, "Yüz algılama model dosyası bulunamadı.");
                return;
            }

            if (faces.Count == 0)
            {
                SetResult("Yüz yok", Palette.Muted, 0, _recognizer.IsTrained
                    ? $"Model hazır: {_recognizer.LabelCount} kişi."
                    : "Tanıma için önce örnek ekleyip modeli eğitin.");
                return;
            }

            RecognitionResult main = faces.OrderByDescending(f => f.Box.Width * f.Box.Height).First().Result;

            if (!_recognizer.IsTrained)
            {
                SetResult("Yüz algılandı", Palette.Accent, 0, $"Algılanan yüz: {faces.Count}\nTanıma için önce örnek ekleyip modeli eğitin.");
                return;
            }

            string detail = $"Mesafe: {main.Distance:0.0} (eşik {_recognizer.Threshold:0})\n" +
                            $"En yakın: {main.NearestLabel}   •   Yüz: {faces.Count}";
            if (_captureWriter.SavedCount > 0 && _chkCapture.Checked) detail += $"\nKaydedilen yüz: {_captureWriter.SavedCount}";

            SetResult(main.IsKnown ? main.DisplayName : "Bilinmiyor", main.IsKnown ? Palette.Known : Palette.Unknown,
                main.ConfidencePercent, detail);
        }

        private void SetResult(string name, System.Drawing.Color color, int confidence, string detail)
        {
            _lblResultName.Text = confidence > 0 ? $"{name}  %{confidence}" : name;
            _lblResultName.ForeColor = color;
            _confidence.Value = confidence;
            _confidence.BarColor = color;
            _lblResultDetail.Text = detail;
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
                SetStatus($"● Kamera açık — {_fpsFrames} FPS{burst}", Palette.Known);
                _fpsFrames = 0;
                _fpsTimer.Restart();
            }
        }

        // =====================================================================
        // Eğitim
        // =====================================================================

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
                SetStatus($"'{label}' için örnek eklendi. (Modeli yeniden eğitmeyi unutmayın)", Palette.Known);
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
                SetStatus($"'{_burstLabel}' için {BurstSampleCount} örnek eklendi. Şimdi 'Modeli Eğit'e basın.", Palette.Known);
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
                bool saved = SaveModel();

                string message = $"Model eğitildi.\nÖrnek: {result.SampleCount}\nKişi (etiket): {result.LabelCount}";
                if (result.SkippedCount > 0) message += $"\nOkunamayan dosya: {result.SkippedCount}";
                message += saved ? "\n\nModel kaydedildi; bir sonraki açılışta hemen yüklenecek." : "\n\n(Model dosyaya kaydedilemedi.)";
                if (result.LabelCount < 2) message += "\n\nNot: Anlamlı bir tanıma için en az 2 farklı kişi ekleyin.";

                MessageBox.Show(message, "Eğitim", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Model eğitildi ve kaydedildi.", Palette.Known);
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

        // =====================================================================
        // Kişiler (etiket yönetimi)
        // =====================================================================

        private void UpdateDatasetInfo()
        {
            string? selected = _lvPeople.SelectedItems.Count > 0 ? _lvPeople.SelectedItems[0].Text : null;
            var counts = _dataset.GetSampleCounts();

            _lvPeople.BeginUpdate();
            _lvPeople.Items.Clear();
            foreach (var (label, count) in counts)
            {
                var item = new ListViewItem(label);
                item.SubItems.Add(count.ToString());
                if (count < 10) item.ForeColor = Palette.Unknown; // az örnek: turuncu (10-20 önerilir)
                _lvPeople.Items.Add(item);
                if (label == selected) item.Selected = true;
            }
            if (counts.Count == 0)
            {
                _lvPeople.Items.Add(new ListViewItem("Henüz kişi yok") { ForeColor = Palette.Muted });
            }
            _lvPeople.EndUpdate();

            UpdatePeopleButtons();
        }

        private string? SelectedPerson()
        {
            if (_lvPeople.SelectedItems.Count == 0) return null;
            ListViewItem item = _lvPeople.SelectedItems[0];
            return item.SubItems.Count > 1 ? item.Text : null; // "Henüz kişi yok" satırı seçilemez sayılır
        }

        private void UpdatePeopleButtons()
        {
            bool hasSelection = SelectedPerson() != null;
            _btnRename.Enabled = hasSelection;
            _btnDeletePerson.Enabled = hasSelection;
        }

        private void BtnRename_Click(object? sender, EventArgs e)
        {
            string? person = SelectedPerson();
            if (person == null) return;

            string? newName = InputDialog.Show(this, "Yeniden adlandır", $"'{person}' için yeni ad:", person);
            if (string.IsNullOrWhiteSpace(newName) || newName == person) return;

            try
            {
                _dataset.RenameLabel(person, newName);
                UpdateDatasetInfo();
                RetrainAfterDatasetChange();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show($"Ad değiştirilemedi:\n{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnDeletePerson_Click(object? sender, EventArgs e)
        {
            string? person = SelectedPerson();
            if (person == null) return;

            if (MessageBox.Show($"'{person}' ve tüm yüz örnekleri silinsin mi?\nBu işlem geri alınamaz.", "Kişiyi sil",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                int deleted = _dataset.DeleteLabel(person);
                UpdateDatasetInfo();
                RetrainAfterDatasetChange();
                SetStatus($"'{person}' silindi ({deleted} örnek).", Palette.Muted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show($"Silinemedi:\n{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                MessageBox.Show($"Klasör açılamadı:\n{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =====================================================================
        // Ayarlar: eşik ve gizlilik
        // =====================================================================

        private void TrkThreshold_ValueChanged(object? sender, EventArgs e)
        {
            _recognizer.Threshold = _trkThreshold.Value;
            _settings.Threshold = _trkThreshold.Value;
            _settings.Save();
            UpdateThresholdLabel();
        }

        private void UpdateThresholdLabel()
        {
            _lblThreshold.Text = $"Tanıma eşiği: {_trkThreshold.Value}" +
                                 (_trkThreshold.Value == (int)FaceRecognizerService.DefaultThreshold ? " (varsayılan)" : "");
        }

        /// <summary>
        /// Gizlilik modunu açar/kapatır. Kamera açıksa bir sonraki kareden itibaren etkili olur.
        /// Ekran görüntüsü alırken yüzlerin görünmemesi için kullanılır; tanıma doğruluğunu etkilemez.
        /// </summary>
        private void ChkPrivacy_CheckedChanged(object? sender, EventArgs e)
        {
            _processor.PrivacyMode = _chkPrivacy.Checked;
            _settings.PrivacyMode = _chkPrivacy.Checked;
            _settings.Save();

            if (!_camera.IsRunning)
            {
                SetStatus(_chkPrivacy.Checked
                    ? "Gizlilik modu açık. Görüntüyü yeniden seçtiğinizde yüzler bulanık gösterilir."
                    : "Gizlilik modu kapalı.", Palette.Muted);
            }
        }

        // =====================================================================
        // Yardımcılar
        // =====================================================================

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
