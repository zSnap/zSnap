﻿/*
 * Copyright 2014-2016 (c) Liam McSherry <mcsherry.liam@gmail.com>
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at:
 * 
 *      http://www.apache.org/licenses/LICENSE-2.0
 *      
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using System.IO;

using zSnap.API.Storage;

namespace zSnap
{
    /// <summary>
    /// Contains the software's entry point.
    /// </summary>
    public static class Entry
    {
        private static System.Timers.Timer UpdateTimer;
        private static System.Timers.Timer LoadingTimer;
        private static int LoadingTimerCounter;

        /// <summary>
        /// <para>
        /// Checks whether there is an update, and prompts the
        /// user about the update if there is.
        /// </para>
        /// </summary>
        /// <returns>
        /// Returns the result of the dialog box.
        /// </returns>
        private static bool? _checkUpdateAndPrompt()
        {
            if (Interop.InternetCheckConnection())
            {
                UpdateInfo update;
                Metadata.CheckNewVersion(out update);

                if (update.IsAvailable)
                {
                    var upd = new UI.UpdateAvailableDisplayWindow(update);

                    return upd.ShowDialog();
                }
            }

            return null;
        }

        public const string NAMESPACE = "zsnap";
        internal const string 
            SETTING_CAPTUREMODE = "CaptureMode",
            SETTING_DOBACKUP    = "StoreBackups",
            SETTING_DOLOGGING   = "LogUploads",
            SETTING_MODS        = "LoadExternals",
            SETTING_UPDATES     = "CheckUpdates",
            SETTING_HOST        = "HostingService"
            ;

        public static System.Windows.Forms.NotifyIcon NotifyIcon { get; set; }
        public static UI.NotificationContextWindow NotifyContext { get; set; }

        public static void IconStartLoadView()
        {
            LoadingTimerCounter = 0;

            LoadingTimer.Start();
        }
        public static void IconStopLoadView()
        {
            LoadingTimer.Stop();

            NotifyIcon.Icon = Properties.Resources.zSnap_ico_p;
        }

        public static Namespace LoadedSettings { get; set; }
        internal static Namespace DefaultSettings { get; private set; }
        
        /// <summary>
        /// If set to true, clicking the notification icon will not show the context
        /// menu, but will fire the PreventNotifyClick event.
        /// </summary>
        public static bool PreventNotifyDisplay { get; set; }
        internal static event Action PreventNotifyClick;

        static Entry()
        {
            #region Notification Icon setup
            NotifyIcon = new System.Windows.Forms.NotifyIcon()
            {
                Icon = zSnap.Properties.Resources.zSnap_ico_p,
                Text = "zSnap",
            };
            #endregion

            #region Default settings
            try
            {
                DefaultSettings = new Namespace(
                    NAMESPACE,
                    new Dictionary<string, string>()
                    {
                        { SETTING_DOBACKUP, true.ToString() },
                        { SETTING_MODS, true.ToString() },
                        { SETTING_UPDATES, true.ToString() },
                        { SETTING_DOLOGGING, true.ToString() }
                    }
                );
            }
            catch (TypeInitializationException tiex)
            {
                if (tiex.InnerException != null)
                    if (tiex.InnerException.GetType() == typeof(StorageInaccessibleException))
                    {

                        System.Windows.MessageBox.Show(
                            String.Format(
                                "{0}\n\n{1}\n{2}\n\n\n{3}\n\n\"{4}\"",
                                "zSnap could not start.",
                                "The file used by zSnap to store settings could not be opened.",
                                "It may be in use by another program, or may have incorrect permission settings.",
                                "Provided below is the error message generated:",
                                tiex.InnerException.InnerException.Message
                            ),
                            "Error Starting",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error,
                            System.Windows.MessageBoxResult.OK
                        );

                        Environment.Exit(-1);
                    }
                    else throw tiex;
            }
            DefaultSettings.Lock();
            #endregion

            #region Set up loading icon timer
            LoadingTimer = new System.Timers.Timer(400);
            LoadingTimer.Elapsed += (s, e) =>
            {
                NotifyIcon.Icon = (LoadingTimerCounter++ % 2) == 0
                    ? Properties.Resources.zSnap_load_icon
                    : Properties.Resources.zSnap_24_p_load2
                    ;
            };
            #endregion

            #region Check API keys
            var keys = typeof(Keys)
                .GetFields()
                .Where(f => f.FieldType == typeof(string));

            if (keys.Count() == 0 ||
                keys.Any(f => String.IsNullOrEmpty(f.GetValue(null).ToString())))
            {
                MessageBox.Show(
                    "One or more API keys is not defined.",
                    "Missing API Keys",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                Environment.Exit(-1);
            }
            #endregion
        }

        [STAThread]
        public static void Main(string[] args)
        {
            // Windows XP is NT 5.x
            // Windows Vista, 7, 8, and 8.1 are NT 6.x
            // Windows 10 is NT 10.0
            if (Environment.OSVersion.Version.Major < 6)
            {
                MessageBox.Show(
                    "zSnap does not support Windows operating systems older than Windows Vista.",
                    "Unsupported Platform",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );

                return;
            }

            #region Load settings
            Namespace lNs = Storage.GetNamespace(NAMESPACE);
            if (lNs == null)
            {
                Storage.pStore.Add(DefaultSettings);
                Storage.Writeback();
                Main(args);
                return;
            }
            else lNs.Lock();

            foreach (string key in DefaultSettings.Keys)
            {
                if (lNs.Keys.Contains(key)) continue;
                else
                {
                    lNs[key] = DefaultSettings[key];
                }
            }
            #endregion

            #region Initial update check + set up timer
            bool doCheckUpdates;
            if (!lNs.RetrieveSafe(SETTING_UPDATES, out doCheckUpdates))
            {
                lNs[SETTING_UPDATES] = DefaultSettings[SETTING_UPDATES];
                doCheckUpdates = bool.Parse(DefaultSettings[SETTING_UPDATES]);
            }
            

            UpdateTimer = new System.Timers.Timer(1000 * 60 * 30); // 30min in ms
            UpdateTimer.Elapsed += (s, e) => NotifyContext.Dispatcher.Invoke(() =>
            {
                // Stop the timer so we don't prompt an absent user (who won't
                // close the window) multiple times.
                UpdateTimer.Stop();

                // We don't need to do anything else here.
                //
                // Either the user accepts the update and the program closes, or
                // the user rejects the update and we don't restart the timer so
                // that we don't bother them.
            });

            if (doCheckUpdates)
            {
                _checkUpdateAndPrompt();
                UpdateTimer.Start();
            }
            #endregion

            NotifyContext = new UI.NotificationContextWindow();
            NotifyIcon.Visible = true;
            NotifyIcon.MouseDown += NotificationClick;

            // We need the window to load so we can bind our hotkey,
            // but we don't want to show it to the user on first start.
            // This seems to work well enough; on the test system, the
            // window is never visible but its Loaded event still fires.
            NotifyContext.Show();
            NotifyContext.Hide();

            Application.Run();
        }
        public static void Quit()
        {
            NotifyIcon.Visible = false;

            Application.Exit();
        }

        internal static void NotificationClick(object sender, EventArgs e)
        {
            if (NotifyContext.IsVisible)
            {
                NotifyContext.Hide();
                return;
            }

            if (!PreventNotifyDisplay) NotifyContext.Show();
            else if (PreventNotifyClick != null) PreventNotifyClick();
        }
    }
}
