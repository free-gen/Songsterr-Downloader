using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System.Windows.Threading;
using System.Threading;
using System.Diagnostics;

namespace TabDownloader
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _httpClient;
        private List<TabItem> _searchResults = new List<TabItem>();
        private readonly string _logPath = "log.txt";

        public MainWindow()
        {
            InitializeComponent();
            
            _httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            });
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://www.songsterr.com");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.songsterr.com/");

            File.WriteAllText(_logPath, $"=== Session started at {DateTime.Now} ===\n");
            LogMessage("Enter artist or song name to search tabs");
        }

        private void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logMessage = $"[{timestamp}] {message}";

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => LogMessage(message));
                return;
            }

            LogTextBox.Text = message;

            try
            {
                File.AppendAllText(_logPath, logMessage + "\n");
            }
            catch (Exception ex)
            {
                LogTextBox.Text = $"Failed to write to log: {ex.Message}";
            }
        }

        private async Task<List<TabItem>> GetSearchUrlsAsync(string searchQuery)
        {
            try
            {
                _searchResults = new List<TabItem>();
                int page = 0;
                const int pageSize = 100;
                bool hasMoreResults = true;

                while (hasMoreResults)
                {
                    string apiUrl = $"https://www.songsterr.com/api/songs?pattern={Uri.EscapeDataString(searchQuery)}&size={pageSize}&from={page * pageSize}";
                    
                    var response = await _httpClient.GetStringAsync(apiUrl);
                    
                    var songs = JArray.Parse(response);
                    if (songs.Count == 0)
                    {
                        hasMoreResults = false;
                        continue;
                    }

                    foreach (var song in songs)
                    {
                        try 
                        {
                            string artist = song["artist"]?.ToString();
                            string title = song["title"]?.ToString();
                            string songId = song["songId"]?.ToString();
                            
                            if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(songId))
                            {
                                string tabUrl = $"https://www.songsterr.com/a/wsa/{artist.ToLower().Replace(" ", "-")}-{title.ToLower().Replace(" ", "-")}-tab-s{songId}";
                                string formattedTitle = $"{ToTitleCase(artist)} - {ToTitleCase(title)} ({songId})";
                                
                                _searchResults.Add(new TabItem 
                                { 
                                    Title = formattedTitle,
                                    Url = tabUrl
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Error parsing song: {ex.Message}");
                            continue;
                        }
                    }

                    LogMessage($"Loaded {_searchResults.Count} tabs...");
                    page++;
                    await Task.Delay(500);
                }

                return _searchResults;
            }
            catch (Exception ex)
            {
                LogMessage($"Error getting search results: {ex.Message}");
                if (ex.InnerException != null)
                {
                    LogMessage($"Inner error: {ex.InnerException.Message}");
                }
                return new List<TabItem>();
            }
        }

        private string ToTitleCase(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            var words = text.Split(' ');
            
            for (int i = 0; i < words.Length; i++)
            {
                if (!string.IsNullOrEmpty(words[i]))
                {
                    words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
                }
            }
            
            return string.Join(" ", words);
        }

        private async Task DownloadTabAsync(string url)
        {
            const int maxRetries = 3;
            const int retryDelay = 2000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var response = await _httpClient.GetStringAsync(url);

                    var match = Regex.Match(response, @"<script id=""state"" type=""application/json"">(.*?)</script>");
                    if (!match.Success)
                    {
                        LogMessage($"Failed to parse tab page, retrying... ({attempt}/{maxRetries})");
                        await Task.Delay(retryDelay);
                        continue;
                    }

                    var state = JObject.Parse(match.Groups[1].Value);
                    var current = state["meta"]["current"];

                    string artist = current["artist"].ToString();
                    string title = current["title"].ToString();
                    string revisionId = current["revisionId"].ToString();
                    string sourceUrl = current["source"].ToString();
                    string extension = Path.GetExtension(sourceUrl).TrimStart('.');

                    string filename = Regex.Replace($"{artist} - {title} ({revisionId}).{extension}", @"[<>:""/\\|?*]", "");
                    string folder = Path.Combine("Tabs", artist);
                    Directory.CreateDirectory(folder);

                    string filePath = Path.Combine(folder, filename);
                    if (File.Exists(filePath))
                    {
                        LogMessage($"File already exists: {filePath}");
                        return;
                    }

                    byte[] tabData = await _httpClient.GetByteArrayAsync(sourceUrl);

                    using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                    {
                        await fileStream.WriteAsync(tabData, 0, tabData.Length);
                    }

                    LogMessage($"Tab successfully downloaded: {filePath}");
                    Dispatcher.Invoke(() => ProgressBar.Value++);
                    return;
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("429"))
                {
                    if (attempt < maxRetries)
                    {
                        var waitTime = retryDelay * attempt;
                        LogMessage($"Too many requests for {url}, attempt {attempt}/{maxRetries}. Waiting {waitTime/1000} sec...");
                        await Task.Delay(waitTime);
                        continue;
                    }
                    LogMessage($"Download failed: too many requests for {url}");
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries)
                    {
                        var waitTime = retryDelay * (attempt <= 3 ? 1 : 2);
                        LogMessage($"Error downloading {url}: {ex.Message}, retrying in {waitTime/1000}s...");
                        await Task.Delay(waitTime);
                        continue;
                    }
                    LogMessage($"Download failed: {ex.Message}");
                    return;
                }
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Search();
            }
        }

        private async void Search()
        {
            string artist = SearchTextBox.Text.Trim();
            if (string.IsNullOrEmpty(artist))
            {
                LogMessage("Enter artist name");
                return;
            }

            SearchResultsListView.Visibility = Visibility.Collapsed;
            DownloadAllButton.Visibility = Visibility.Collapsed;
            SearchResultsListView.ItemsSource = null;
            _searchResults.Clear();
            
            try
            {
                LogMessage("Searching for tabs...");
                var results = await GetSearchUrlsAsync(artist);
                
                if (results.Any())
                {
                    SearchResultsListView.ItemsSource = results;
                    SearchResultsListView.Visibility = Visibility.Visible;
                    DownloadAllButton.Visibility = Visibility.Visible;
                    
                    LogMessage($"Found {results.Count} tabs");
                }
                else
                {
                    LogMessage("No tabs found");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Search failed: {ex.Message}");
            }
        }

        private async void DownloadAllButton_Click(object sender, RoutedEventArgs e)
        {
            SearchResultsListView.IsEnabled = false;
            DownloadAllButton.IsEnabled = false;
            int downloadedCount = 0;

            try
            {
                int total = _searchResults.Count;
                LogMessage($"Starting download of {total} tabs");
                ProgressBar.Maximum = total;
                ProgressBar.Value = 0;

                var semaphore = new SemaphoreSlim(10);
                var tasks = new List<Task>();

                foreach (var tab in _searchResults)
                {
                    await semaphore.WaitAsync();
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadTabAsync(tab.Url);
                            Interlocked.Increment(ref downloadedCount);
                            LogMessage($"Downloaded {downloadedCount} of {total} tabs");
                            await Task.Delay(1000);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);
                LogMessage($"Download completed! Successfully downloaded: {downloadedCount} of {total} tabs");
            }
            finally
            {
                DownloadAllButton.IsEnabled = true;
                SearchResultsListView.IsEnabled = true;
                ProgressBar.Value = 0;
            }
        }

        private async void DownloadSingleTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string url)
            {
                button.IsEnabled = false;
                SearchResultsListView.IsEnabled = false;
                DownloadAllButton.IsEnabled = false;

                try
                {
                    ProgressBar.Maximum = 1;
                    ProgressBar.Value = 0;
                    
                    LogMessage($"Downloading tab: {url}");
                    
                    using (var cts = new CancellationTokenSource())
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(30));
                        
                        try
                        {
                            await Task.Run(async () => 
                            {
                                await DownloadTabAsync(url);
                            }, cts.Token);
                            
                            LogMessage("Download completed!");
                        }
                        catch (OperationCanceledException)
                        {
                            LogMessage("Download timed out. Please try again.");
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Download failed: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    button.IsEnabled = true;
                    SearchResultsListView.IsEnabled = true;
                    DownloadAllButton.IsEnabled = true;
                    ProgressBar.Value = 0;
                    await Task.Delay(2000);
                }
            }
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Clear();
            SearchTextBox.Focus();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchTextBox.Template.FindName("ClearButton", SearchTextBox) is Button clearButton)
            {
                clearButton.Visibility = string.IsNullOrEmpty(SearchTextBox.Text) 
                    ? Visibility.Collapsed 
                    : Visibility.Visible;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            LogMessage("=== Session ended ===");
            base.OnClosed(e);
            _httpClient.Dispose();
        }
    }

    public class TabItem
    {
        public string Title { get; set; }
        public string Url { get; set; }
    }
} 