using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BO1Tracker
{
    public enum OverlayMode { Bo1Bonus, Bo2Bonus, Box }

    public partial class OverlayWindow : Window
    {
        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlackOpsTracker", "save.json");

        private FileSystemWatcher? _watcher;
        private bool _locked = false;
        private bool _showSpecialRounds = true;
        private bool _showZombiesHordes = true;
        private readonly MainWindow? _owner;

        // ── Taille globale de l'overlay (cases, images, texte) ────────────
        // Un LayoutTransform sur tout le contenu : tout rétrécit ensemble, sans
        // jamais changer l'ordre ni la disposition choisis par l'utilisateur.
        private static readonly double[] ScaleLevels = [1.0, 0.85, 0.7, 0.55];
        private int _scaleIndex = 0;

        public OverlayMode Mode { get; private set; }

        // ── État BO1/BO2 Bonus ───────────────────────────────────────────
        private List<(int Order, string Label, string ImagePath)> _bo1Clicks = [];
        private List<(int Order, string Label, string ImagePath)> _bo2Clicks = [];
        
        

        // ── État Boîte ───────────────────────────────────────────────────
        private List<(int Order, string Location)> _boxVisits   = [];
        private HashSet<string>                     _boxSelected = [];
        private string?                             _lastVisited = null;
        

        // ── Couleurs (correspondant exactement au tracker) ───────────────
        private static readonly Color ColAccent     = (Color)ColorConverter.ConvertFromString("#1D9E75");
        private static readonly Color ColAccentLight = (Color)ColorConverter.ConvertFromString("#2DD4A2");
        private static readonly Color ColAccentDark  = (Color)ColorConverter.ConvertFromString("#0D3327");

        // ── "Rouge zombie" : utilisé uniquement pour les manches spéciales et les hordes,
        // pour les distinguer visuellement du vert des bonus.
        private static readonly Color ColZombieRed      = (Color)ColorConverter.ConvertFromString("#B3122A");
        private static readonly Color ColZombieRedLight = (Color)ColorConverter.ConvertFromString("#FF4D5E");
        private static readonly Color ColZombieRedDark  = (Color)ColorConverter.ConvertFromString("#330608");
        private static readonly Color ColBgCard      = (Color)ColorConverter.ConvertFromString("#1A1A1A");
        private static readonly Color ColBorder      = (Color)ColorConverter.ConvertFromString("#2A2A2A");
        private static readonly Color ColTextSecond  = (Color)ColorConverter.ConvertFromString("#888888");
        private static readonly Color ColYellow      = (Color)ColorConverter.ConvertFromString("#FFD700");
        private static readonly Color ColYellowDark  = (Color)ColorConverter.ConvertFromString("#B8860B");
        private static readonly Color ColBgYellow    = (Color)ColorConverter.ConvertFromString("#3D3000");

        public OverlayWindow(OverlayMode mode = OverlayMode.Bo1Bonus, MainWindow? owner = null)
        {
            InitializeComponent();
            Mode = mode;
            _owner = owner;
            LoadOverlaySettings();

            _saveSettingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveSettingsTimer.Tick += (_, _) => { _saveSettingsTimer.Stop(); SaveOverlaySettings(); };
            SizeChanged     += (_, _) => ScheduleSaveSettings();
            LocationChanged += (_, _) => ScheduleSaveSettings();

            Loaded += OnLoaded;
        }

        // ── Mémorisation de la taille/position/réglages de l'overlay ─────
        // Fichier séparé de save.json (que l'overlay surveille) pour ne pas déclencher de rafraîchissement.
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlackOpsTracker", "overlay.json");

        private sealed class OverlaySettings
        {
            public bool   HasGeometry       { get; set; }
            public double Left              { get; set; }
            public double Top               { get; set; }
            public double Width             { get; set; }
            public double Height            { get; set; }
            public int    ScaleIndex        { get; set; }
            public bool   ShowSpecialRounds { get; set; } = true;
            public bool   ShowZombiesHordes { get; set; } = true;
        }

        private readonly DispatcherTimer _saveSettingsTimer;

        private void ScheduleSaveSettings()
        {
            if (!IsLoaded) return; // ignore les changements de taille/position faits pendant l'ouverture
            _saveSettingsTimer.Stop();
            _saveSettingsTimer.Start();
        }

        private void LoadOverlaySettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var s = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(SettingsPath));
                if (s == null) return;

                _scaleIndex        = Math.Clamp(s.ScaleIndex, 0, ScaleLevels.Length - 1);
                _showSpecialRounds = s.ShowSpecialRounds;
                _showZombiesHordes = s.ShowZombiesHordes;
                RoundsVisibilityBtn.Content = _showSpecialRounds ? "MANCHES" : "MANCHES ✕";
                HordesVisibilityBtn.Content = _showZombiesHordes ? "HORDES"  : "HORDES ✕";

                if (s.HasGeometry && s.Width >= MinWidth && s.Height >= MinHeight)
                {
                    Width  = s.Width;
                    Height = s.Height;

                    // Position rétablie seulement si la fenêtre reste visible (écran débranché, résolution changée...).
                    double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
                    double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
                    bool visible = s.Left + s.Width > vl + 80 && s.Left < vl + vw - 80
                                && s.Top + 40 > vt && s.Top < vt + vh - 40;
                    if (visible)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = s.Left;
                        Top  = s.Top;
                    }
                }
            }
            catch { }
        }

        /// <summary>Enregistre taille, position et réglages (taille globale, manches/hordes affichées).</summary>
        internal void SaveOverlaySettings()
        {
            try
            {
                if (!IsLoaded) return;
                var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;

                var s = new OverlaySettings
                {
                    HasGeometry = true,
                    Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
                    ScaleIndex = _scaleIndex,
                    ShowSpecialRounds = _showSpecialRounds,
                    ShowZombiesHordes = _showZombiesHordes,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
            }
            catch { }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateModeButtons();
            ApplyOpaqueState(startOpaque: true); // ouverture en opaque par défaut (doit précéder ReadAndRefresh
                                                  // pour que les cases soient construites avec le bon état dès le départ)
            ReadAndRefresh();
            ApplyScale();

            var dir = Path.GetDirectoryName(SavePath)!;
            Directory.CreateDirectory(dir);

            _watcher = new FileSystemWatcher(dir, Path.GetFileName(SavePath))
            {
                NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ReadAndRefresh);
        }

        // ── Lecture JSON ─────────────────────────────────────────────────
        private void ReadAndRefresh()
        {
            try
            {
                if (!File.Exists(SavePath)) return;

                string? json = null;
                for (int i = 0; i < 5; i++)
                {
                    try { json = File.ReadAllText(SavePath); break; }
                    catch (IOException) { System.Threading.Thread.Sleep(60); }
                }
                if (json == null) return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                
                

                _bo1Clicks = ParseClicks(root, "ClickOrder");
                _bo2Clicks = ParseClicks(root, "Bo2ClickOrder");

                // Boîte
                
                _lastVisited   = root.TryGetProperty("LastBoxLocation", out var lb)  ? lb.GetString()  : null;
                _boxVisits   = [];
                _boxSelected = [];
                if (root.TryGetProperty("BoxVisits", out var bv))
                    foreach (var v in bv.EnumerateArray())
                    {
                        var loc = v.TryGetProperty("Location", out var l) ? l.GetString() ?? "" : "";
                        var ord = v.TryGetProperty("Order",    out var o) ? o.GetInt32()        : 0;
                        _boxVisits.Add((ord, loc));
                    }
                if (root.TryGetProperty("BoxSelected", out var bs))
                    foreach (var s in bs.EnumerateArray())
                        _boxSelected.Add(s.GetString() ?? "");

                RebuildCards();
            }
            catch { }
        }

        private static List<(int Order, string Label, string ImagePath)> ParseClicks(JsonElement root, string key)
        {
            var result = new List<(int, string, string)>();
            if (!root.TryGetProperty(key, out var arr)) return result;
            foreach (var v in arr.EnumerateArray())
            {
                int ord = v.TryGetProperty("Order", out var o) ? o.GetInt32() : 0;
                string lbl = "", img = "";
                if (v.TryGetProperty("Drop", out var d))
                {
                    if (d.TryGetProperty("Label",     out var l)) lbl = l.GetString() ?? "";
                    if (d.TryGetProperty("ImagePath", out var p)) img = p.GetString() ?? "";
                }
                result.Add((ord, lbl, img));
            }
            return result;
        }

        // ── Construction cartes ──────────────────────────────────────────
        internal void RebuildCards()
        {
            CardsPanel.Children.Clear();
            RoundSectionPanel.Children.Clear();
            RoundSectionPanel.Visibility = Visibility.Collapsed;
            ZombiesSectionPanel.Children.Clear();
            ZombiesSectionPanel.Visibility = Visibility.Collapsed;

            switch (Mode)
            {
                case OverlayMode.Bo1Bonus:
                    if (_owner != null) BuildBonusCardsLive(_owner.CurrentDropsForOverlay, _owner.SelectedForOverlay, _owner.ClickOrderForOverlay, isBo2: false);
                    else                 BuildBonusCards(_bo1Clicks);
                    if (_showSpecialRounds) BuildBo1RoundSection();
                    break;
                case OverlayMode.Bo2Bonus:
                    if (_owner != null) BuildBonusCardsLive(_owner.CurrentBo2DropsForOverlay, _owner.Bo2SelectedForOverlay, _owner.Bo2ClickOrderForOverlay, isBo2: true);
                    else                 BuildBonusCards(_bo2Clicks);
                    if (_showSpecialRounds) BuildBo2RoundSection();
                    break;
                case OverlayMode.Box:
                    BuildBoxCards();
                    break;
            }

            // Zombies par manche : indépendant de la map/du mode, affiché tant que le tracker est ouvert.
            if (_showZombiesHordes) BuildZombiesSection();

            // Le bouton BONUS n'a de sens qu'en mode Bo1Bonus / Bo2Bonus (pas pour la boîte)
            BonusTypesBtn.Visibility = Mode == OverlayMode.Box ? Visibility.Collapsed : Visibility.Visible;
            if (Mode == OverlayMode.Box) BonusTypesPanel.Visibility = Visibility.Collapsed;
            if (BonusTypesPanel.Visibility == Visibility.Visible) BuildBonusTypesPanel();
        }

        // ── Afficher/masquer les manches spéciales et les hordes dans l'overlay ──
        private void RoundsVisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            _showSpecialRounds = !_showSpecialRounds;
            RoundsVisibilityBtn.Content = _showSpecialRounds ? "MANCHES" : "MANCHES ✕";
            RebuildCards();
            ScheduleSaveSettings();
        }

        private void HordesVisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            _showZombiesHordes = !_showZombiesHordes;
            HordesVisibilityBtn.Content = _showZombiesHordes ? "HORDES" : "HORDES ✕";
            RebuildCards();
            ScheduleSaveSettings();
        }

        // ── Activer/désactiver les bonus optionnels (Carpenter, Fire Sale, ...) depuis l'overlay ──
        private void BonusTypesButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = BonusTypesPanel.Visibility != Visibility.Visible;
            BonusTypesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) BuildBonusTypesPanel();
        }

        private void BuildBonusTypesPanel()
        {
            BonusTypesPanel.Children.Clear();
            if (_owner == null) return;

            var items = Mode == OverlayMode.Bo2Bonus ? _owner.Bo2ToggleablesForOverlay : _owner.Bo1ToggleablesForOverlay;
            bool isBo2 = Mode == OverlayMode.Bo2Bonus;

            foreach (var (id, label, enabled) in items)
                BonusTypesPanel.Children.Add(MakeBonusTypeChip(id, label, enabled, isBo2));
        }

        private Border MakeBonusTypeChip(string id, string label, bool enabled, bool isBo2)
        {
            var tb = new TextBlock
            {
                Text       = (enabled ? "● " : "○ ") + label.ToUpper(),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(enabled ? ColAccentLight : ColTextSecond),
            };

            var chip = new Border
            {
                Child           = tb,
                Background      = new SolidColorBrush(enabled ? ColAccentDark : ColBgCard),
                BorderBrush     = new SolidColorBrush(enabled ? ColAccent : ColBorder),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(14),
                Padding         = new Thickness(10, 4, 10, 4),
                Margin          = new Thickness(0, 0, 6, 0),
                Cursor          = Cursors.Hand,
                Opacity         = enabled ? 1.0 : 0.65,
            };

            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (_owner == null) return;
                if (isBo2) _owner.ToggleBo2DropEnabledFromOverlay(id);
                else       _owner.ToggleBo1DropEnabledFromOverlay(id);
                RebuildCards(); // reconstruit aussi ce panneau puisqu'il est toujours visible
            };

            return chip;
        }

        /// <summary>Affiche TOUS les bonus disponibles (verts si déjà cliqués, orange pour le dernier en date)
        /// et permet de cliquer directement dans l'overlay pour les activer/désactiver.</summary>
        private void BuildBonusCardsLive(List<DropItem> drops, HashSet<string> selected, List<ClickedDrop> clickOrder, bool isBo2)
        {
            int maxOrder = clickOrder.Count > 0 ? clickOrder.Max(c => c.Order) : 0;
            foreach (var drop in drops)
            {
                var click = clickOrder.FirstOrDefault(c => c.Drop.Id == drop.Id);
                int? order = click?.Order;
                bool isLast = order.HasValue && order.Value == maxOrder && maxOrder > 0;
                CardsPanel.Children.Add(MakeLiveBonusCard(drop, selected.Contains(drop.Id), order, isLast, isBo2));
            }

            bool empty = CardsPanel.Children.Count == 0;
            EmptyText.Text        = "Sélectionne une map dans le tracker...";
            EmptyText.Visibility  = empty ? Visibility.Visible  : Visibility.Collapsed;
            CardsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border MakeLiveBonusCard(DropItem drop, bool isSelected, int? order, bool isLast, bool isBo2)
        {
            var bgColor     = isLast ? ColBgYellow    : (isSelected ? ColAccentDark  : ColBgCard);
            var borderColor = isLast ? ColYellow      : (isSelected ? ColAccent      : ColBorder);
            var labelColor  = isLast ? ColYellow      : (isSelected ? ColAccentLight : ColTextSecond);
            var badgeColor  = isLast ? ColYellowDark  : ColAccent;

            var orderBadge = new Border
            {
                Background          = new SolidColorBrush(badgeColor),
                CornerRadius        = new CornerRadius(10),
                Padding             = new Thickness(6, 1, 6, 1),
                Margin              = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility          = order.HasValue ? Visibility.Visible : Visibility.Hidden,
                Child = new TextBlock
                {
                    Text       = order.HasValue ? $"{order}" : "",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                }
            };

            var img = new Image
            {
                Width = 52, Height = 52, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 6),
                Source = LoadDropImage(drop.ImagePath),
            };

            var lblBlock = new TextBlock
            {
                Text          = drop.Label.ToUpper(),
                FontFamily    = new FontFamily("Consolas"),
                FontSize      = 11,
                FontWeight    = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(labelColor),
                MaxWidth      = 90,
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(orderBadge);
            stack.Children.Add(img);
            stack.Children.Add(new Border { Height = 6 });
            stack.Children.Add(lblBlock);

            // BorderThickness et absence d'effet de lueur (DropShadowEffect) fixés en dur :
            // avant, ils changeaient selon l'état (sélectionné / dernier), ce qui faisait
            // "gonfler" visuellement la carte au clic dans OBS. Seules les couleurs
            // (fond / bordure / texte) indiquent maintenant l'état, la taille ne bouge jamais.
            var card = new Border
            {
                Child           = stack,
                Width           = 120, // largeur FIXE (pas MinWidth) : toutes les cases ont exactement la même taille,
                                        // peu importe la longueur du libellé (ex. "NUKE" vs "DOUBLE POINTS").
                Padding         = new Thickness(14, 12, 14, 12),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                // En mode transparent (bouton OPAQUE/TRANSPARENT), le fond coloré de chaque case
                // disparaît aussi : il ne reste que l'image/le texte flottant, comme le reste de l'overlay.
                Background      = _opaqueBackground ? new SolidColorBrush(bgColor) : Brushes.Transparent,
                BorderBrush     = new SolidColorBrush(borderColor),
                Margin          = new Thickness(0, 0, 12, 0),
                Cursor          = Cursors.Hand,
            };

            card.MouseLeftButtonUp += (_, _) =>
            {
                if (_owner == null) return;
                if (isBo2) _owner.ToggleBo2DropFromOverlay(drop.Id);
                else       _owner.ToggleDropFromOverlay(drop.Id);
                RebuildCards();
            };

            return card;
        }

        /// <summary>Section "manche spéciale" pour l'onglet BO1 : uniquement sur les maps qui en ont
        /// vraiment (Kino, Five, Ascension, Shi No Numa, Der Riese), sinon rien.</summary>
        private void BuildBo1RoundSection()
        {
            if (_owner == null || !_owner.IsBo1DogMapActive) return;
            BuildRoundSection(
                title: "SPECIAL ROUND",
                current: _owner.CurrentDogRoundForOverlay,
                steps: [4, 5],
                formatRound: r => _owner.FormatRoundForOverlay(r),
                onAdvance: step => _owner.AdvanceDogRoundFromOverlay(step),
                getStepCount: step => _owner.DogStepCountForOverlay(step));
        }

        /// <summary>Section "manche spéciale" pour l'onglet BO2 : Die Rise (+4/+5) ou Mob of the Dead (+3/+4/+5)
        /// selon la map actuellement sélectionnée dans le tracker, sinon rien.</summary>
        private void BuildBo2RoundSection()
        {
            if (_owner == null) return;

            if (_owner.IsBo2DieRiseMapActive)
            {
                BuildRoundSection(
                    title: "SPECIAL ROUND (DIE RISE)",
                    current: _owner.CurrentBo2DogRoundForOverlay,
                    steps: [4, 5],
                    formatRound: r => _owner.FormatRoundForOverlay(r),
                    onAdvance: step => _owner.AdvanceBo2DogRoundFromOverlay(step),
                    getStepCount: step => _owner.Bo2DogStepCountForOverlay(step));
            }
            else if (_owner.IsBo2MobMapActive)
            {
                BuildRoundSection(
                    title: "SPECIAL ROUND (MOB OF THE DEAD)",
                    current: _owner.CurrentBo2MobRoundForOverlay,
                    steps: [4, 5, 6],
                    formatRound: r => $"{r}",
                    onAdvance: step => _owner.AdvanceBo2MobRoundFromOverlay(step),
                    getStepCount: step => _owner.Bo2MobStepCountForOverlay(step));
            }
        }

        private void BuildRoundSection(string title, int? current, int[] steps, Func<int, string> formatRound, Action<int> onAdvance, Func<int, int> getStepCount)
        {
            RoundSectionPanel.Visibility = Visibility.Visible;

            var currentBlock = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            currentBlock.Children.Add(new TextBlock
            {
                Text = title, FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(ColTextSecond), Margin = new Thickness(0, 0, 0, 2),
            });
            currentBlock.Children.Add(new TextBlock
            {
                Text = current.HasValue ? formatRound(current.Value) : "—",
                FontFamily = new FontFamily("Consolas"), FontSize = 22, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColZombieRedLight),
            });
            RoundSectionPanel.Children.Add(currentBlock);

            if (current.HasValue)
                foreach (int step in steps)
                    RoundSectionPanel.Children.Add(MakeRoundStepBadge(step, formatRound(current.Value + step), onAdvance));
        }

        private Border MakeRoundStepBadge(int step, string nextRoundLabel, Action<int> onAdvance)
        {
            var label = new TextBlock
            {
                Text = nextRoundLabel, FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            };

            var badge = new Border
            {
                Child           = label,
                Background      = new SolidColorBrush(ColZombieRedDark),
                BorderThickness = new Thickness(0),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(12, 8, 12, 8),
                Margin          = new Thickness(0, 0, 8, 0),
                Cursor          = Cursors.Hand,
            };

            badge.MouseLeftButtonUp += (_, _) =>
            {
                onAdvance(step);
                RebuildCards();
            };

            return badge;
        }

        // ── Zombies par manche (indépendant de la map/du mode) ────────────
        private void BuildZombiesSection()
        {
            if (_owner == null) return; // nécessite le tracker ouvert (lecture/écriture de son état)

            ZombiesSectionPanel.Visibility = Visibility.Visible;

            int round  = _owner.CurrentZombiesRoundForOverlay;
            double hordes = _owner.ZombiesHordesForOverlay;

            // Manche, modifiable directement : -1 / +1, ou en tapant un numéro dans le champ.
            var roundRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var roundControls = new StackPanel { Orientation = Orientation.Horizontal };
            roundControls.Children.Add(MakeZombiesStepButton("−", () => { _owner.AdvanceZombiesRoundFromOverlay(-1); RebuildCards(); }));

            var roundInput = new TextBox
            {
                Text = $"{round}",
                FontFamily = new FontFamily("Consolas"), FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColZombieRedLight), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), CaretBrush = new SolidColorBrush(ColZombieRedLight),
                TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                MinWidth = 48, Margin = new Thickness(8, 0, 8, 0),
            };
            void CommitRoundInput()
            {
                if (int.TryParse(roundInput.Text, out int newRound) && newRound > 0 && newRound != round)
                {
                    _owner.SetZombiesRoundFromOverlay(newRound);
                    RebuildCards();
                }
                else
                {
                    roundInput.Text = $"{round}"; // valeur invalide ou inchangée : on réaffiche la manche actuelle
                }
            }
            roundInput.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            roundInput.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitRoundInput(); e.Handled = true; }
            };
            roundInput.LostFocus += (_, _) => CommitRoundInput();
            roundControls.Children.Add(roundInput);

            roundControls.Children.Add(MakeZombiesStepButton("+", () => { _owner.AdvanceZombiesRoundFromOverlay(1); RebuildCards(); }));
            roundRow.Children.Add(roundControls);
            ZombiesSectionPanel.Children.Add(roundRow);

            // Nombre de hordes à la manche + nombre de joueurs choisis dans l'appli.
            var hordesBlock = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            hordesBlock.Children.Add(new TextBlock
            {
                Text = "HORDES", FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(ColTextSecond), Margin = new Thickness(0, 0, 0, 2),
            });
            hordesBlock.Children.Add(new TextBlock
            {
                Text = hordes.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), FontFamily = new FontFamily("Consolas"), FontSize = 22, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColZombieRedLight),
            });
            ZombiesSectionPanel.Children.Add(hordesBlock);
        }

        private Border MakeZombiesStepButton(string label, Action onClick)
        {
            var btn = new Border
            {
                Child = new TextBlock
                {
                    Text = label, FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                },
                Background      = new SolidColorBrush(ColZombieRedDark),
                BorderThickness = new Thickness(0),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 4, 10, 4),
                Cursor          = Cursors.Hand,
            };
            btn.MouseLeftButtonUp += (_, _) => onClick();
            return btn;
        }

        private void BuildBonusCards(List<(int Order, string Label, string ImagePath)> clicks)
        {
            int maxOrder = clicks.Count > 0 ? clicks.Max(c => c.Order) : 0;
            foreach (var (ord, lbl, imgPath) in clicks.OrderBy(c => c.Order))
                CardsPanel.Children.Add(MakeBonusCard(lbl, imgPath, ord, ord == maxOrder && maxOrder > 0));

            bool empty = CardsPanel.Children.Count == 0;
            EmptyText.Text        = "En attente du premier bonus...";
            EmptyText.Visibility  = empty ? Visibility.Visible  : Visibility.Collapsed;
            CardsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border MakeBonusCard(string label, string imagePath, int order, bool isLast)
        {
            var bgColor     = isLast ? ColBgYellow  : ColAccentDark;
            var borderColor = isLast ? ColYellow     : ColAccent;
            var labelColor  = isLast ? ColYellow     : ColAccentLight;
            var badgeColor  = isLast ? ColYellowDark : ColAccent;

            // Badge numéro
            var orderBadge = new Border
            {
                Background          = new SolidColorBrush(badgeColor),
                CornerRadius        = new CornerRadius(10),
                Padding             = new Thickness(6, 1, 6, 1),
                Margin              = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = $"{order}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                }
            };

            // Image du bonus
            var img = new Image
            {
                Width               = 52,
                Height              = 52,
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Source              = LoadDropImage(imagePath),
            };

            var lblBlock = new TextBlock
            {
                Text          = label.ToUpper(),
                FontFamily    = new FontFamily("Consolas"),
                FontSize      = 11,
                FontWeight    = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(labelColor),
                MaxWidth      = 90,
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(orderBadge);
            stack.Children.Add(img);
            stack.Children.Add(new Border { Height = 6 });
            stack.Children.Add(lblBlock);

            var card = new Border
            {
                Child           = stack,
                Width           = 120, // largeur FIXE (pas MinWidth) : toutes les cases ont exactement la même taille,
                                        // peu importe la longueur du libellé (ex. "NUKE" vs "DOUBLE POINTS").
                Padding         = new Thickness(14, 12, 14, 12),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                // En mode transparent (bouton OPAQUE/TRANSPARENT), le fond coloré de chaque case
                // disparaît aussi : il ne reste que l'image/le texte flottant, comme le reste de l'overlay.
                Background      = _opaqueBackground ? new SolidColorBrush(bgColor) : Brushes.Transparent,
                BorderBrush     = new SolidColorBrush(borderColor),
                Margin          = new Thickness(0, 0, 12, 0),
            };

            return card;
        }

        // ── Cartes Boîte ─────────────────────────────────────────────────
        private void BuildBoxCards()
        {
            int maxOrder = _boxVisits.Count > 0 ? _boxVisits.Max(v => v.Order) : 0;
            foreach (var (ord, loc) in _boxVisits.OrderBy(v => v.Order))
                CardsPanel.Children.Add(MakeBoxCard(loc, ord, ord == maxOrder && maxOrder > 0, false));

            if (_lastVisited != null && !_boxSelected.Contains(_lastVisited))
                CardsPanel.Children.Add(MakeBoxCard(_lastVisited, null, false, true));

            bool empty = CardsPanel.Children.Count == 0;
            EmptyText.Text        = "En attente du premier emplacement...";
            EmptyText.Visibility  = empty ? Visibility.Visible  : Visibility.Collapsed;
            CardsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border MakeBoxCard(string location, int? order, bool isLast, bool isMemo)
        {
            bool highlight  = isLast || isMemo;
            var bgColor     = highlight ? ColBgYellow : (order.HasValue ? ColAccentDark : ColBgCard);
            var borderColor = highlight ? ColYellow   : (order.HasValue ? ColAccent     : ColBorder);
            var labelColor  = highlight ? ColYellow   : (order.HasValue ? ColAccentLight : ColTextSecond);
            var badgeColor  = highlight ? ColYellowDark : ColAccent;

            var orderBadge = new Border
            {
                Background          = new SolidColorBrush(badgeColor),
                CornerRadius        = new CornerRadius(10),
                Padding             = new Thickness(6, 1, 6, 1),
                Margin              = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility          = order.HasValue ? Visibility.Visible : Visibility.Hidden,
                Child = new TextBlock
                {
                    Text       = order.HasValue ? $"{order}" : "",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                }
            };

            var img = new Image
            {
                Width               = 52,
                Height              = 52,
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 6),
                Source              = LoadLocationImage(location),
            };

            var lbl = new TextBlock
            {
                Text          = location.ToUpper(),
                FontFamily    = new FontFamily("Consolas"),
                FontSize      = 11,
                FontWeight    = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(labelColor),
                MaxWidth      = 90,
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(orderBadge);
            stack.Children.Add(img);
            stack.Children.Add(new Border { Height = 6 });
            stack.Children.Add(lbl);

            var card = new Border
            {
                Child           = stack,
                Width           = 120, // largeur FIXE (pas MinWidth) : toutes les cases ont exactement la même taille,
                                        // peu importe la longueur du libellé (ex. "NUKE" vs "DOUBLE POINTS").
                Padding         = new Thickness(14, 12, 14, 12),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                // En mode transparent (bouton OPAQUE/TRANSPARENT), le fond coloré de chaque case
                // disparaît aussi : il ne reste que l'image/le texte flottant, comme le reste de l'overlay.
                Background      = _opaqueBackground ? new SolidColorBrush(bgColor) : Brushes.Transparent,
                BorderBrush     = new SolidColorBrush(borderColor),
                Margin          = new Thickness(0, 0, 12, 0),
            };

            return card;
        }

        // ── Chargement images (pack URI = ressources embarquées) ─────────
        private static BitmapImage? TryLoadImage(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            try
            {
                var uri = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.UriSource = uri; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>Icônes de drops/emplacements : fond noir intégré à l'image, blanc pour le dessin.
        /// Mode OPAQUE : image telle quelle. Mode TRANSPARENT : fond noir rendu transparent (par
        /// luminance), pour que seul le dessin blanc flotte, comme le reste de l'overlay.</summary>
        private readonly Dictionary<string, ImageSource?> _blackKeyedImageCache = [];

        private ImageSource? LoadDropImage(string relativePath)
        {
            var bmp = TryLoadImage(relativePath);
            return ApplyBlackKeyIfTransparent(bmp, relativePath);
        }

        private ImageSource? LoadLocationImage(string location)
        {
            string path = $"Images/locations/{location}.png";
            var bmp = TryLoadImage(path);
            if (bmp == null) { path = "Images/mystery_box.png"; bmp = TryLoadImage(path); }
            return ApplyBlackKeyIfTransparent(bmp, path);
        }

        private ImageSource? ApplyBlackKeyIfTransparent(BitmapImage? bmp, string cacheKey)
        {
            if (bmp == null) return null;
            if (_opaqueBackground) return bmp;

            if (!_blackKeyedImageCache.TryGetValue(cacheKey, out var keyed))
            {
                keyed = MakeBlackTransparent(bmp);
                _blackKeyedImageCache[cacheKey] = keyed;
            }
            return keyed ?? bmp;
        }

        /// <summary>Convertit le fond noir d'une icône en transparence, en se basant sur la luminance
        /// de chaque pixel (noir = alpha 0, blanc = alpha plein) : le dessin blanc reste intact,
        /// seul le carré noir qui l'entoure disparaît.</summary>
        private static BitmapSource? MakeBlackTransparent(BitmapSource src)
        {
            try
            {
                var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int width = converted.PixelWidth, height = converted.PixelHeight;
                if (width <= 0 || height <= 0) return src;
                int stride = width * 4;
                var pixels = new byte[height * stride];
                converted.CopyPixels(pixels, stride, 0);

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                    double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                    pixels[i + 3] = (byte)(a * luminance);
                }

                var wb = new WriteableBitmap(width, height, converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                wb.Freeze();
                return wb;
            }
            catch { return src; }
        }

        // ── Sélecteur de mode ────────────────────────────────────────────
        private void UpdateModeButtons()
        {
            var active   = new SolidColorBrush(Color.FromArgb(200, 29, 158, 117));
            var inactive = new SolidColorBrush(Color.FromArgb(50,  255, 255, 255));
            var fgActive   = Brushes.White;
            var fgInactive = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

            ModeBO1Btn.Background = Mode == OverlayMode.Bo1Bonus ? active : inactive;
            ModeBO2Btn.Background = Mode == OverlayMode.Bo2Bonus ? active : inactive;
            ModeBoxBtn.Background = Mode == OverlayMode.Box      ? active : inactive;
            ModeBO1Btn.Foreground = Mode == OverlayMode.Bo1Bonus ? fgActive : fgInactive;
            ModeBO2Btn.Foreground = Mode == OverlayMode.Bo2Bonus ? fgActive : fgInactive;
            ModeBoxBtn.Foreground = Mode == OverlayMode.Box      ? fgActive : fgInactive;
        }

        private void ModeBO1_Click(object sender, RoutedEventArgs e) { Mode = OverlayMode.Bo1Bonus; UpdateModeButtons(); RebuildCards(); }
        private void ModeBO2_Click(object sender, RoutedEventArgs e) { Mode = OverlayMode.Bo2Bonus; UpdateModeButtons(); RebuildCards(); }
        private void ModeBox_Click (object sender, RoutedEventArgs e) { Mode = OverlayMode.Box;      UpdateModeButtons(); RebuildCards(); }

        // ── Drag / contrôles ─────────────────────────────────────────────
        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_locked && e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void LockButton_Click(object sender, RoutedEventArgs e)
        {
            _locked = !_locked;
            LockBtn.Content = _locked ? "UNLOCK" : "LOCK";
        }

        private void TopButton_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            TopBtn.Content = Topmost ? "FOND" : "DEVANT";
        }

        /// <summary>Rétrécit tout l'overlay (cases, images, texte) d'un cran à chaque clic, en boucle
        /// sur quelques paliers. Un seul LayoutTransform sur tout le contenu : la disposition et l'ordre
        /// choisis (cartes, manches, zombies) ne bougent jamais, tout rapetisse simplement ensemble.</summary>
        private void ScaleButton_Click(object sender, RoutedEventArgs e)
        {
            _scaleIndex = (_scaleIndex + 1) % ScaleLevels.Length;
            ApplyScale();
            ScheduleSaveSettings();
        }

        private void ApplyScale()
        {
            double scale = ScaleLevels[_scaleIndex];
            RootStackPanel.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
            ScaleBtn.Content = $"TAILLE {(int)Math.Round(scale * 100)}%";
        }

        private bool _opaqueBackground = false;
        private static readonly Brush RootOpaqueBrush        = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D0D0D"));
        private static readonly Brush HeaderBarDragBrush     = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#01000000")); // quasi invisible mais cliquable
        private static readonly Brush HeaderBarOpaqueBrush   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DD111111"));
        private static readonly Brush ContentBarOpaqueBrush  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CC111111"));

        /// <summary>Bascule entre fond TOTALEMENT transparent (rien que les cartes flottent à l'écran —
        /// idéal pour OBS) et fond plein (pratique pour utiliser la fenêtre normalement sur le bureau,
        /// sans passer par OBS). La barre du haut garde toujours un fond quasi invisible (alpha=1)
        /// même en mode transparent, sinon la fenêtre ne serait plus déplaçable.</summary>
        private void ApplyOpaqueState(bool startOpaque)
        {
            _opaqueBackground = startOpaque;

            RootGrid.Background          = _opaqueBackground ? RootOpaqueBrush        : Brushes.Transparent;
            HeaderBarBorder.Background   = _opaqueBackground ? HeaderBarOpaqueBrush   : HeaderBarDragBrush;
            ContentBarBorder.Background  = _opaqueBackground ? ContentBarOpaqueBrush  : Brushes.Transparent;

            OpaqueBtn.Content = _opaqueBackground ? "TRANSPARENT" : "OPAQUE";
        }

        private void OpaqueButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyOpaqueState(!_opaqueBackground);
            RebuildCards(); // reconstruit aussi les cases pour appliquer le fond transparent/opaque
        }

        private bool _headerCollapsed = false;

        /// <summary>Réduit la barre du haut aux seuls boutons ▲/▼ + fermeture,
        /// pour n'afficher que les bonus à l'écran (pratique une fois les réglages faits).</summary>
        private void CollapseButton_Click(object sender, RoutedEventArgs e)
        {
            _headerCollapsed = !_headerCollapsed;
            HeaderControlsPanel.Visibility      = _headerCollapsed ? Visibility.Collapsed : Visibility.Visible;
            HeaderRightControlsPanel.Visibility = _headerCollapsed ? Visibility.Collapsed : Visibility.Visible;
            CollapseBtn.Content = _headerCollapsed ? "▼" : "▲";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void OverlayWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _saveSettingsTimer.Stop();
            SaveOverlaySettings();
            _watcher?.Dispose();
        }
    }
}
