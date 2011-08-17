﻿
#region Using Statements

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Forms = System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;

using AniDBmini.Collections;
using AniDBmini.HashAlgorithms;

#endregion

namespace AniDBmini
{
    public partial class MainWindow : Window
    {

        #region Fields

        private const string AniDBaLink = @"http://anidb.net/perl-bin/animedb.pl?show=anime&aid=";

        public static string m_AppName = Application.ResourceAssembly.GetName().Name;

        private Forms.NotifyIcon m_notifyIcon;
        private Forms.ContextMenu m_notifyContextMenu = new Forms.ContextMenu();
        private WindowState m_storedWindowState = WindowState.Normal;

        private BackgroundWorker m_HashWorker;

        private AniDBAPI aniDB;
        private MPCAPI mpcApi;
        private MylistDB m_myList;

        private DateTime m_hashingStartTime;
        private Object m_hashingLock = new Object();

        private int m_storedTabIndex;
        private bool isHashing;
        private double totalQueueSize, ppSize;

        private string[] allowedVideoFiles = { "*.avi", "*.mkv", "*.mov", "*.mp4", "*.mpeg", "*.mpg", "*.ogm" };

        private TSObservableCollection<MylistStat> mylistStatsList = new TSObservableCollection<MylistStat>();
        private TSObservableCollection<HashItem> hashFileList = new TSObservableCollection<HashItem>();
        private TSObservableCollection<AnimeTab> animeTabList = new TSObservableCollection<AnimeTab>();

        #endregion Fields

        #region Constructor

        public MainWindow(AniDBAPI api)
        {
            aniDB = api;
            AniDBAPI.AppendDebugLine("Welcome to AniDBmini, connected to: " + aniDB.APIServer);

            InitializeComponent();
            SetMylistVisibility();

            mylistStats.ItemsSource = mylistStatsList;
            debugListBox.ItemsSource = aniDB.DebugLog;
            hashingListBox.ItemsSource = hashFileList;
            animeTabControl.ItemsSource = animeTabList;

            animeTabList.OnCountChanged += new CountChangedHandler(animeTabList_OnCountChanged);

            aniDB.OnFileHashingProgress += new FileHashingProgressHandler(OnFileHashingProgress);
            aniDB.OnAnimeTabFetched += new AnimeTabFetchedHandler(OnAnimeTabFetched);
            aniDB.OnFileInfoFetched += new FileInfoFetchedHandler(OnFileInfoFetched);
        }

        #endregion Constructor

        #region Initialize

        /// <summary>
        /// Retrieves and formats mylist stats.
        /// </summary>
        private void InitializeStats()
        {
            int[] stats = aniDB.MyListStats();
            int i = 0;

            foreach (int stat in stats)
            {
                string text = AniDBAPI.statsText[i];
                string value;

                if (text != "x")
                {
                    if (i == 3)
                        value = ((double)stat).ToFormatedBytes(ExtensionMethods.BYTE_UNIT.MB, ExtensionMethods.BYTE_UNIT.GB);
                    else if (i == 16)
                    {
                        int days = (int)Math.Floor((stat / 60f) / 24f);
                        int hours = (int)Math.Floor((((stat / 60f) / 24f) - (int)Math.Floor((stat / 60f) / 24f)) * 24);
                        int minutes = (int)((Math.Round((((stat / 60f) / 24f) - (int)Math.Floor((stat / 60f) / 24f)) * 24, 2) - hours) * 60);
                        value = days + "d " + hours + "h " + minutes + "m";
                    }
                    else if (i >= 10 && i <= 12)
                        value = stat + "%";
                    else
                        value = stat.ToString();

                    mylistStatsList.Add(new MylistStat(text, value));
                }

                i++;
            }
        }

        /// <summary>
        /// Initializes the tray icon.
        /// </summary>
        private void InitializeNotifyIcon() // TODO: add options (minimize on close, disable tray icon, always show, etc.)
        {
            m_notifyIcon = new Forms.NotifyIcon();
            m_notifyIcon.Text = this.Title;
            m_notifyIcon.Icon = new System.Drawing.Icon(global::AniDBmini.Properties.Resources.AniDBmini, 16, 16);
            m_notifyIcon.MouseDoubleClick += (s, e) => { this.Show(); WindowState = m_storedWindowState; };
            m_notifyIcon.ContextMenu = m_notifyContextMenu;

            Forms.MenuItem cm_open = new Forms.MenuItem();
            cm_open.Text = "Open";
            cm_open.Click += (s, e) => { Show(); WindowState = m_storedWindowState; };
            m_notifyContextMenu.MenuItems.Add(cm_open);

            Forms.MenuItem cm_MPCHCopen = new Forms.MenuItem();
            cm_MPCHCopen.Text = "Open MPC-HC";
            cm_MPCHCopen.Click += (s, e) => { mpchcLaunch(this, null); };
            m_notifyContextMenu.MenuItems.Add(cm_MPCHCopen);

            m_notifyContextMenu.MenuItems.Add("-");

            Forms.MenuItem cm_exit = new Forms.MenuItem();
            cm_exit.Text = "Exit";
            cm_exit.Click += (s, e) => { this.Close(); };
            m_notifyContextMenu.MenuItems.Add(cm_exit);
        }

        #endregion

        #region Private Methods

        #region Hashing

        /// <summary>
        /// Creates a entry and adds it to the hash list.
        /// </summary>
        /// <param name="path">Path to file.</param>
        private void addRowToHashTable(string path)
        {
            HashItem item = new HashItem(path);

            lock (m_hashingLock)
                hashFileList.Add(item);

            if (isHashing)
                totalQueueSize += item.Size;
            else if (!isHashing)
                Dispatcher.BeginInvoke(new Action(delegate { hashingStartButton.IsEnabled = true; }));
        }

        /// <summary>
        /// Removes a hash entry from the list.
        /// </summary>
        /// <param name="item">Item to remove.</param>
        /// <param name="userRemoved">True if removed by user.</param>
        private void removeRowFromHashTable(HashItem item, bool userRemoved = false)
        {
            if (isHashing && userRemoved)
            {
                totalQueueSize -= item.Size;

                if (item == hashFileList[0])
                    aniDB.cancelHashing();
            }

            lock (m_hashingLock)
                hashFileList.Remove(item);

            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (hashFileList.Count == 0)
                    hashingStartButton.IsEnabled = hashingStopButton.IsEnabled = false;
            }));
        }

        /// <summary>
        /// Initializes the hashing background worker.
        /// </summary>
        private void beginHashing()
        {
            hashingStartButton.IsEnabled = false;
            hashingStopButton.IsEnabled = isHashing = true;

            totalQueueSize = 0;

            for (int i = 0; i < hashFileList.Count; i++)
                totalQueueSize += hashFileList[i].Size;

            m_HashWorker = new BackgroundWorker();
            m_HashWorker.WorkerSupportsCancellation = true;

            m_HashWorker.DoWork += new DoWorkEventHandler(OnHashWorkerDoWork);
            m_HashWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(OnHashWorkerCompleted);

            m_hashingStartTime = DateTime.Now;
            m_HashWorker.RunWorkerAsync();
        }

        /// <summary>
        /// Adds completed hash item to mylist.
        /// </summary>
        private void FinishHash(HashItem item)
        {
            ppSize += item.Size;

            if (addToMyListCheckBox.IsChecked == true)
            {
                item.Viewed = Convert.ToInt32(watchedCheckBox.IsChecked);
                item.State = stateComboBox.SelectedIndex;

                aniDB.MyListAdd(item);
            }
        }

        #endregion Hashing

        #region Mylist

        /// <summary>
        /// Sets mylist visibility based on sql connection.
        /// </summary>
        private void SetMylistVisibility()
        {
            if (m_myList.isSQLConnOpen)
            {
                MylistImortButton.Visibility = Visibility.Collapsed;
                MylistDataGrid.IsEnabled = true;
            }
            else
            {
                MylistImortButton.Visibility = Visibility.Visible;
                MylistDataGrid.IsEnabled = false;
            }
        }

        /// <summary>
        /// Expands or contracts a datagridrow's details.
        /// </summary>
        public void MylistToggleEntry(DependencyObject src)
        {
            DataGridRow row = src.FindAncestor<DataGridRow>();

            if (row != null)
            {
                if (row.Item is AnimeEntry)
                {
                    AnimeEntry entry = (AnimeEntry)row.Item;
                    entry.IsExpanded = !entry.IsExpanded;
                    if (entry.IsExpanded && !entry.IsFetched)
                    {
                        entry.Episodes = m_myList.SelectEpisodes(entry);
                        entry.IsFetched = true;
                    }
                }
                else if (row.Item is EpisodeEntry)
                {
                    EpisodeEntry entry = (EpisodeEntry)row.Item;
                    if (entry.genericOnly)
                        return;
                    else
                    {
                        entry.IsExpanded = !entry.IsExpanded;
                        if (entry.IsExpanded && !entry.IsFetched)
                        {
                            entry.Files = m_myList.SelectFiles(entry);
                            entry.IsFetched = true;
                        }
                    }
                }
            }
        }

        #endregion Mylist

        #endregion Private Methods

        #region Events

        #region Main Window

        private void OnInitialized(object sender, EventArgs e)
        {
            m_myList = new MylistDB();
            MylistDataGrid.ItemsSource = m_myList.Entries;

            InitializeStats();
            InitializeNotifyIcon();
        }

        private void ShowOpionsWindow(object sender, RoutedEventArgs e)
        {
            OptionsWindow options = new OptionsWindow();
            options.Owner = this;
            if (options.ShowDialog() == true && mpcApi != null) mpcApi.LoadConfig();
        }

        /// <summary>
        /// Launches or focuses MPC-HC.
        /// </summary>
        private void mpchcLaunch(object sender, RoutedEventArgs e)
        {
            if (mpcApi == null || !mpcApi.isHooked || !mpcApi.FocusMPC())
            {
                if (File.Exists(ConfigFile.Read("mpcPath").ToString()))
                {
                    mpcApi = new MPCAPI(this);
                    mpcApi.OnFileWatched += new FileWatchedHandler(OnFileWatched);                    
                }
                else
                    MessageBox.Show("Media Player Classic - Home Cinema not found!\n" +
                                    "Please ensure you have located the mpc-hc executable inside the options.", "Error!", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnFileWatched(object sender, FileWatchedArgs e)
        {
            HashItem item = new HashItem(e.Path);
            item.State = item.Viewed = 1;

            ThreadPool.QueueUserWorkItem(new WaitCallback(delegate
            {
                item = aniDB.ed2kHash(item);
                aniDB.MyListAdd(item);

                if (ConfigFile.Read("mpcShowOSD").ToBoolean() && mpcApi != null && mpcApi.isHooked)
                    mpcApi.ShowWatchedOSD();
            }));
        }

        private void randomAnimeButton_Click(object sender, RoutedEventArgs e)
        {
            Button btn = (Button)sender;
            ContextMenu cm = btn.ContextMenu;
            cm.PlacementTarget = btn;
            cm.IsOpen = true;
        }

        private void randomAnimeLabelContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MenuItem mi = (MenuItem)sender;
            aniDB.RandomAnime(int.Parse(mi.Tag.ToString()));
        }

        private void OnAnimeTabFetched(AnimeTab aTab)
        {
            animeTabList.Add(aTab);
        }

        private void OnFileInfoFetched(FileInfoFetchedArgs e)
        {
            if (!m_myList.isSQLConnOpen)
            {
                Dispatcher.Invoke(new Action(m_myList.Create));
                Dispatcher.BeginInvoke(new Action(SetMylistVisibility));
            }

            m_myList.InsertFileInfo(e);

            Dispatcher.BeginInvoke(new Action(delegate
            {
                m_myList.SelectEntry(e.Anime.aid);
                MylistDataGrid.Items.SortDescriptions.Add(new SortDescription("title", ListSortDirection.Ascending));
            }));
        }

        private void mainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((TabItem)mainTabControl.SelectedItem != animeTabItem)
                m_storedTabIndex = mainTabControl.SelectedIndex;
        }

        private void OnStateChanged(object sender, EventArgs args)
        {
            if (this.WindowState == WindowState.Minimized)
                this.Hide();
            else
                m_storedWindowState = this.WindowState;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (m_notifyIcon != null)
                m_notifyIcon.Visible = !this.IsVisible;
        }

        private void OnClose(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (ConfigFile.Read("mpcClose").ToBoolean() && mpcApi != null && mpcApi.isHooked)
                mpcApi.CloseMPC();

            m_myList.Close();
            aniDB.Logout();

            m_notifyIcon.Dispose();
            m_notifyIcon = null;
        }

        #endregion Main Window

        #region Home Tab

        private void clearDebugLog(object sender, RoutedEventArgs e)
        {
            aniDB.DebugLog.Clear();
        }

        private void debugListBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (debugListBoxScrollViewer.VerticalOffset == debugListBoxScrollViewer.ScrollableHeight)
                debugListBoxScrollViewer.ScrollToBottom();
        }

        #endregion Home Tab

        #region Hashing Tab

        private void hashingListBox_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                Array.Sort(files);

                for (int i = 0; i < files.Length; i++)
                {
                    FileInfo fi = new FileInfo(files[i]);

                    if (allowedVideoFiles.Contains<string>("*" + fi.Extension.ToLower()))
                        addRowToHashTable(fi.FullName);
                }
            }
        }

        private void removeSelectedHashItems(object sender, RoutedEventArgs e)
        {
            while (hashingListBox.SelectedItems.Count > 0)
                removeRowFromHashTable((HashItem)hashingListBox.SelectedItems[0], true);
        }

        private void clearHashItems(object sender, RoutedEventArgs e)
        {
            if (isHashing)
                hashingStopButton_Click(this, null);

            hashFileList.Clear();
        }

        private void hashingListBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
                removeSelectedHashItems(sender, null);
        }

        private void startHashingButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isHashing)
                beginHashing();
        }

        private void hashingStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (isHashing)
            {
                m_HashWorker.CancelAsync();
                aniDB.cancelHashing();

                OnHashWorkerCompleted(sender, null);
            }
        }

        private void OnHashWorkerDoWork(object sender, DoWorkEventArgs e)
        {
            while (hashFileList.Count > 0 && isHashing)
            {
                if (m_HashWorker.CancellationPending)
                {
                    e.Cancel = true;
                    return;
                }

                HashItem _temp = aniDB.ed2kHash(hashFileList[0]);

                if (isHashing && _temp != null) // if we did not abort remove item from queue and process
                {
                    hashFileList[0] = _temp;
                    Dispatcher.BeginInvoke(new Action<HashItem>(FinishHash), hashFileList[0]);
                    removeRowFromHashTable(hashFileList[0]);
                }
            }
        }

        private void OnHashWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            hashingStopButton.IsEnabled = isHashing = false;
            fileProgressBar.Value = totalProgressBar.Value = ppSize = 0;
            hashingStartButton.IsEnabled = hashFileList.Count > 0;
            timeRemainingTextBlock.Text = timeElapsedTextBlock.Text = totalBytesTextBlock.Text = String.Empty;

            m_HashWorker.Dispose();
        }

        private void addFilesButton_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Video Files|" + String.Join(";", allowedVideoFiles) + "|All Files|*.*";
            dlg.Multiselect = true;

            Nullable<bool> result = dlg.ShowDialog();

            if (result == true)
            {
                ThreadPool.QueueUserWorkItem(new WaitCallback(delegate
                {
                    for (int i = 0; i < dlg.FileNames.Length; i++)
                        addRowToHashTable(dlg.FileNames[i]);
                }));
            }
        }

        private void addFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            Forms.FolderBrowserDialog dlg = new Forms.FolderBrowserDialog();
            dlg.ShowNewFolderButton = false;

            Forms.DialogResult result = dlg.ShowDialog();
            if (result == Forms.DialogResult.OK)
            {
                ThreadPool.QueueUserWorkItem(new WaitCallback(delegate
                {
                    foreach (string _file in Directory.GetFiles(dlg.SelectedPath, "*.*")
                                                      .Where(s => allowedVideoFiles.Contains("*" + Path.GetExtension(s).ToLower())))
                        addRowToHashTable(_file);

                    foreach (string dir in Directory.GetDirectories(dlg.SelectedPath))
                        try
                        {
                            foreach (string _file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                                                              .Where(s => allowedVideoFiles.Contains("*" + Path.GetExtension(s).ToLower())))
                                addRowToHashTable(_file);
                        }
                        catch (UnauthorizedAccessException) { }
                }));
            }
        }

        private void OnFileHashingProgress(object sender, FileHashingProgressArgs e)
        {
            double fileProg = e.ProcessedSize / e.TotalSize * 100;
            double totalProg = (e.ProcessedSize + ppSize) / totalQueueSize * 100;

            TimeSpan totalTimeElapsed = DateTime.Now - m_hashingStartTime;
            TimeSpan remainingSpan = TimeSpan.FromSeconds(totalQueueSize * (totalTimeElapsed.TotalSeconds / (ppSize + e.ProcessedSize)) - totalTimeElapsed.TotalSeconds - 0.5);

            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (isHashing)
                {
                    timeElapsedTextBlock.Text = String.Format("Elapsed: {0}", totalTimeElapsed.ToFormatedString());
                    timeRemainingTextBlock.Text = String.Format("ETA: {0}", remainingSpan.ToFormatedString());
                    totalBytesTextBlock.Text = String.Format("Bytes: {0} / {1}", (e.ProcessedSize + ppSize).ToFormatedBytes(ExtensionMethods.BYTE_UNIT.GB),
                                                                                 totalQueueSize.ToFormatedBytes(ExtensionMethods.BYTE_UNIT.GB));

                    fileProgressBar.Value = fileProg;
                    totalProgressBar.Value = totalProg;
                }
            }));
        }

        #endregion Hashing Tab

        #region Mylist Tab

        private void ImportList_Click(object sender, RoutedEventArgs e)
        {
            ImportWindow import = new ImportWindow(m_myList);
            import.Owner = this;

            MylistImortButton.Visibility = Visibility.Collapsed;

            import.ShowDialog();
            SetMylistVisibility();
        }

        private void MylistDataGridSorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;

            MylistDataGrid.EnableRowVirtualization = true;
            ListSortDirection direction = (e.Column.SortDirection != ListSortDirection.Ascending) ? ListSortDirection.Ascending : ListSortDirection.Descending;
            e.Column.SortDirection = direction;

            ListCollectionView lcv = (ListCollectionView)CollectionViewSource.GetDefaultView(MylistDataGrid.ItemsSource);
            MylistSort mylistSort = new MylistSort(direction, e.Column);
            lcv.CustomSort = mylistSort;
            MylistDataGrid.EnableRowVirtualization = false;
        }

        private void ExpandExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            MylistToggleEntry((DependencyObject)e.OriginalSource);
            e.Handled = true;
        }

        private void MylistExpand_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                DependencyObject src = (DependencyObject)e.OriginalSource;
                if (src.FindAncestor<Button>() == null && src.FindAncestor<DataGrid>().Name != "filesDataGrid")
                    MylistToggleEntry(src);
            }
            catch (NullReferenceException) { }

            e.Handled = true;
        }

        private void OpenADBPage_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(AniDBaLink + ((MenuItem)sender).Tag.ToString());
        }
        
        private void EntryViewDetails_Click(object sender, RoutedEventArgs e)
        {
            aniDB.Anime(int.Parse(((MenuItem)sender).Tag.ToString()));
        }

        private void OpenFileWithMPCHC_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = (MenuItem)sender;

            if (item.Tag != null)
            {
                string filePath = item.Tag.ToString();
                mpchcLaunch(this, null);

                ThreadPool.QueueUserWorkItem(new WaitCallback(delegate
                {
                    while (!mpcApi.isHooked) Thread.Sleep(200);
                    mpcApi.OpenFile(filePath);
                }));
            }
        }

        private void MarkWatched_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = (MenuItem)sender;
            var row = ((ContextMenu)item.Parent).PlacementTarget as DataGridRow;
            System.Diagnostics.Debug.WriteLine(row);
        }

        private void MarkUnwatched_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = (MenuItem)sender;
            var row = ((ContextMenu)item.Parent).PlacementTarget as DataGridRow;
            System.Diagnostics.Debug.WriteLine(row);
        }

        private void MylistDGRDVChanged(object sender, DataGridRowDetailsEventArgs e)
        {
            DataGrid dg = (DataGrid)sender;
            if (dg != null && e.Row.DetailsVisibility == Visibility.Collapsed)
            {
                DataGrid childDG = ((DependencyObject)e.Row).FindChild<DataGrid>();
                if (childDG != null)
                    childDG.UnselectAll();
            }
        }

        /// <summary>
        /// Fixes height bug.
        /// </summary>
        private void episodesDGRDVChanged(object sender, DataGridRowDetailsEventArgs e)
        {
            DataGrid dg = sender as DataGrid;
            if (dg != null && e.Row.DetailsVisibility == Visibility.Collapsed) dg.Items.Refresh();
        }

        #endregion Mylist Tab

        #region Anime Tab

        private void OnTabCloseClick(object sender, RoutedEventArgs e)
        {
            Button s = (Button)sender;
            animeTabList.RemoveAll(x => x.AnimeID == int.Parse(s.Tag.ToString()));
        }

        private void animeTabList_OnCountChanged(object sender, CountChangedArgs e)
        {
            if (e.oldCount == 1 && e.newCount == 0) // no more tabs
            {
                animeTabItem.Visibility = System.Windows.Visibility.Collapsed;
                mainTabControl.SelectedIndex = m_storedTabIndex;
            }
            else
            {
                if (e.oldCount == 0 && e.newCount == 1) // first tab
                {
                    animeTabItem.Visibility = System.Windows.Visibility.Visible;
                    m_storedTabIndex = mainTabControl.SelectedIndex;
                }

                animeTabItem.Focus();
                animeTabControl.SelectedIndex = e.newCount - 1;
            }
        }

        private void AnimeURLClick(object sender, RoutedEventArgs e)
        {
            Image s_img = (Image)sender;
            System.Diagnostics.Process.Start(s_img.Tag.ToString());
        }

        #endregion Anime Tab

        #endregion

        #region Properties

        public string wTitle
        {
            get { return this.Title; }
            set
            {
                this.Title = value;
                m_notifyIcon.Text = value.Truncate(63, false, true);
            }
        }

        #endregion

    }

    #region Custom Application Commands

    public static class Command
    {
        public static readonly RoutedUICommand Expand = new RoutedUICommand("Expand Entry", "Expand", typeof(MainWindow));
    }

    #endregion Custom Application Commands

}
