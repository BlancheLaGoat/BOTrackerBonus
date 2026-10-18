using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace BO1Tracker;

/// <summary>
/// Vérifie automatiquement, au lancement de l'app, si une nouvelle version a
/// été publiée sur GitHub (la Release "latest" republiée à chaque build par
/// le workflow Actions). Si oui, propose de la télécharger et de remplacer
/// l'exécutable en cours tout seul, puis relance l'app — aucune action
/// manuelle à part confirmer.
///
/// Comme la Release garde toujours le même tag "latest" (son .exe est
/// simplement remplacé à chaque build), on détecte une nouvelle version en
/// comparant la date de publication ("published_at") de la Release à la
/// dernière date vue, mémorisée localement.
/// </summary>
internal static class UpdateChecker
{
    // Dépôt GitHub où le workflow CI republie BO1Tracker.exe sous la Release "latest".
    private const string Owner = "BlancheLaGoat";
    private const string Repo  = "BO1TrackerBonus-github";

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlackOpsTracker", "update.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private sealed class UpdateState
    {
        public string? LastSeenPublishedAt { get; set; }
    }

    private sealed class GhAsset
    {
        public string? name { get; set; }
        public string? browser_download_url { get; set; }
    }

    private sealed class GhRelease
    {
        public string? published_at { get; set; }
        public GhAsset[]? assets { get; set; }
    }

    /// <summary>
    /// Vérifie s'il existe une nouvelle version.
    /// <paramref name="manual"/> = false (appel automatique au démarrage) :
    /// ne dit jamais rien si tout va bien ou en cas d'erreur réseau, ne
    /// propose une mise à jour que si une version plus récente que la
    /// dernière vue est trouvée.
    /// <paramref name="manual"/> = true (bouton "Vérifier les mises à
    /// jour") : donne toujours un retour visuel — à jour, erreur, ou
    /// proposition de mise à jour — même au tout premier lancement.
    /// Ne lève jamais d'exception vers l'appelant.
    /// </summary>
    public static async Task CheckForUpdatesAsync(bool manual = false)
    {
        try
        {
            if (!Http.DefaultRequestHeaders.Contains("User-Agent"))
                Http.DefaultRequestHeaders.Add("User-Agent", "BO1Tracker-UpdateChecker");

            string json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/latest");

            var release = JsonSerializer.Deserialize<GhRelease>(json);
            if (release?.published_at == null || release.assets == null)
            {
                ShowManualError(manual, "Réponse inattendue de GitHub (Release \"latest\" introuvable).");
                return;
            }

            var exeAsset = Array.Find(release.assets,
                a => a.name != null && a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (exeAsset?.browser_download_url == null)
            {
                ShowManualError(manual, "Aucun .exe trouvé dans les assets de la Release \"latest\".");
                return;
            }

            var state = LoadState();

            // Premier lancement (pas d'historique local) : on mémorise la
            // version actuelle sans proposer de "mise à jour" automatiquement.
            if (string.IsNullOrEmpty(state.LastSeenPublishedAt))
            {
                SaveState(new UpdateState { LastSeenPublishedAt = release.published_at });
                if (manual)
                {
                    Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
                        "Aucun historique local : la version actuelle vient d'être enregistrée comme référence.\n\n" +
                        "Publie une nouvelle version sur GitHub puis relance cette vérification pour la détecter.",
                        "Vérification des mises à jour",
                        MessageBoxButton.OK, MessageBoxImage.Information));
                }
                return;
            }

            if (!DateTime.TryParse(release.published_at, out var remoteDate) ||
                !DateTime.TryParse(state.LastSeenPublishedAt, out var localDate))
            {
                ShowManualError(manual, "Impossible de lire les dates de version.");
                return;
            }

            if (remoteDate <= localDate)
            {
                if (manual)
                {
                    Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
                        "Tu es déjà sur la dernière version disponible.",
                        "Vérification des mises à jour",
                        MessageBoxButton.OK, MessageBoxImage.Information));
                }
                return; // Rien de plus récent que ce qu'on a déjà vu.
            }

            bool accepted = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                accepted = MessageBox.Show(
                    "Une nouvelle version de BO1 Tracker est disponible.\n\n" +
                    "La télécharger et l'installer maintenant ? L'application va se fermer puis redémarrer automatiquement.",
                    "Mise à jour disponible",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes;
            });

            if (!accepted)
            {
                // Refusé : on redemandera au prochain lancement (on ne
                // mémorise pas cette version comme "vue").
                return;
            }

            await DownloadAndInstallAsync(exeAsset.browser_download_url, release.published_at);
        }
        catch
        {
            // Pas de connexion, API GitHub indisponible, etc. L'appli doit
            // toujours démarrer normalement même sans accès réseau — mais si
            // c'est une vérification manuelle, on prévient l'utilisateur.
            ShowManualError(manual, "Impossible de contacter GitHub (pas de connexion internet ?).");
        }
    }

    private static void ShowManualError(bool manual, string reason)
    {
        if (!manual) return;
        Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
            "Échec de la vérification des mises à jour.\n\n" + reason,
            "Vérification des mises à jour",
            MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    private static async Task DownloadAndInstallAsync(string downloadUrl, string publishedAt)
    {
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe)) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "BO1Tracker_update");
        Directory.CreateDirectory(tempDir);
        string newExePath = Path.Combine(tempDir, "BO1Tracker_new.exe");
        string scriptPath = Path.Combine(tempDir, "update.bat");

        using (var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            using var fs = new FileStream(newExePath, FileMode.Create, FileAccess.Write);
            await resp.Content.CopyToAsync(fs);
        }

        // On mémorise la version qu'on vient de télécharger avant de relancer,
        // pour ne pas réafficher la même invite juste après le redémarrage.
        SaveState(new UpdateState { LastSeenPublishedAt = publishedAt });

        // Petit script qui attend que l'ancien .exe soit libéré (le move
        // échoue tant qu'il est verrouillé par le processus en cours — on
        // vient justement de fermer l'appli), le remplace par la nouvelle
        // version, relance l'appli, puis se supprime lui-même.
        string script =
            "@echo off\r\n" +
            ":retry\r\n" +
            $"move /Y \"{newExePath}\" \"{currentExe}\" >nul 2>&1\r\n" +
            $"if exist \"{newExePath}\" (\r\n" +
            "    timeout /t 1 /nobreak >nul\r\n" +
            "    goto retry\r\n" +
            ")\r\n" +
            $"start \"\" \"{currentExe}\"\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        });

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private static UpdateState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var s = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath));
                if (s != null) return s;
            }
        }
        catch { }
        return new UpdateState();
    }

    private static void SaveState(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch { }
    }
}
