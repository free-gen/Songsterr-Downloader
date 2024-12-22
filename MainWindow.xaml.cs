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
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.Windows.Threading;
using System.Threading;
using System.Diagnostics;

namespace TabDownloader
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _httpClient;
        private IWebDriver _driver;
        private bool _browserReady;
        private readonly DispatcherTimer _messageTimer;
        private readonly Queue<string> _messageQueue;

        public MainWindow()
        {
            InitializeComponent();
            
            _httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            });
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "*/*");

            _messageQueue = new Queue<string>();
            _messageTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _messageTimer.Tick += ProcessMessages;
            _messageTimer.Start();
        }

        private void LogMessage(string message)
        {
            _messageQueue.Enqueue(message);
        }

        private void ProcessMessages(object sender, EventArgs e)
        {
            while (_messageQueue.Count > 0)
            {
                string message = _messageQueue.Dequeue();
                LogTextBox.AppendText(message + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlTextBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                LogMessage("Enter URL");
                return;
            }

            if (!url.StartsWith("https://www.songsterr.com"))
            {
                LogMessage("Invalid link. Link must start with https://www.songsterr.com");
                return;
            }

            DownloadButton.IsEnabled = false;
            try
            {
                if (url.Contains("?pattern="))
                {
                    LogMessage("Search link detected");
                    var tabUrls = await GetSearchUrlsAsync(url);
                    if (tabUrls != null && tabUrls.Any())
                    {
                        int total = tabUrls.Count;
                        LogMessage($"Found {total} tabs");
                        ProgressBar.Maximum = total;
                        ProgressBar.Value = 0;

                        var semaphore = new SemaphoreSlim(10);
                        var tasks = new List<Task>();

                        foreach (var tabUrl in tabUrls)
                        {
                            await semaphore.WaitAsync();
                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    await DownloadTabAsync(tabUrl);
                                    await Task.Delay(500);
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }));
                        }

                        await Task.WhenAll(tasks);
                    }
                }
                else
                {
                    await DownloadTabAsync(url);
                }
            }
            finally
            {
                DownloadButton.IsEnabled = true;
                ProgressBar.Value = 0;
                if (_driver != null)
                {
                    _driver.Quit();
                    _driver = null;
                }
            }
        }

        private async Task<List<string>> GetSearchUrlsAsync(string url)
        {
            try
            {
                var options = new ChromeOptions();
                options.AddArgument("--log-level=3");
                options.AddArgument("--silent");
                options.AddArgument("--window-size=1024,768");
                
                var service = ChromeDriverService.CreateDefaultService();
                service.HideCommandPromptWindow = true;
                
                _driver = new ChromeDriver(service, options);
                try
                {
                    LogMessage("Opening browser. Please scroll to the bottom to load all tabs.");
                    _driver.Navigate().GoToUrl(url);
                    LogMessage("After all tabs are loaded, click 'List Loaded' button");

                    ConfirmButton.Visibility = Visibility.Visible;
                    _browserReady = false;

                    while (!_browserReady)
                    {
                        await Task.Delay(100);
                        if (_driver == null)
                            return null;
                    }

                    LogMessage("Collecting tab links...");
                    var tabElements = _driver.FindElements(By.CssSelector("div[data-list='songs'] a, div[data-list='artist'] a"));
                    var tabUrls = tabElements
                        .Select(elem => elem.GetAttribute("href"))
                        .Where(href => !string.IsNullOrEmpty(href) && href.Contains("/a/wsa/"))
                        .ToList();

                    LogMessage($"Found {tabUrls.Count} tab links");
                    return tabUrls;
                }
                finally
                {
                    ConfirmButton.Visibility = Visibility.Collapsed;
                    if (_driver != null)
                    {
                        _driver.Quit();
                        _driver = null;
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Browser error: {ex.Message}");
                return null;
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            _browserReady = true;
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
                        return;

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
                    File.WriteAllBytes(filePath, tabData);

                    LogMessage($"Tab successfully downloaded: {filePath}");
                    
                    Dispatcher.Invoke(() => ProgressBar.Value++);
                    return;
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("429"))
                {
                    if (attempt < maxRetries)
                    {
                        LogMessage($"Too many requests for {url}, attempt {attempt} of {maxRetries}. Waiting {retryDelay/1000} sec...");
                        await Task.Delay(retryDelay);
                    }
                    else
                    {
                        LogMessage($"Error processing tab {url}: maximum attempts exceeded");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"Error processing tab {url}: {ex.Message}");
                    return;
                }
            }
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UrlTextBox.Text = Clipboard.GetText();
            }
            catch (Exception ex)
            {
                LogMessage($"Error pasting from clipboard: {ex.Message}");
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
} 