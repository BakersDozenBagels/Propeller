﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RT.Propeller;
using RT.PropellerApi;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Threading;

namespace Propeller
{
    sealed class AppDomainInfo
    {
        public AppDomain AppDomain;
        public AppDomainRunner Runner;
    }

    sealed class PropellerEngine : PeriodicMultiple
    {
        private AppDomainInfo _activeAppDomain = null;
        private List<AppDomainInfo> _inactiveAppDomains = new List<AppDomainInfo>();
        private int _appDomainCount = 0;
        private object _lockObject = new object();
        private ListeningThread _currentListeningThread = null;
        private PropellerSettings _currentConfig = null;
        private bool _firstRunEver = true;
        private DateTime _configFileChangeTime = DateTime.MinValue;

        protected override TimeSpan FirstInterval { get { return TimeSpan.Zero; } }

        public PropellerEngine()
        {
#if DEBUG
            var checkInterval = TimeSpan.FromSeconds(1);
#else
            var checkInterval = TimeSpan.FromSeconds(10);
#endif

            Tasks = new List<Task>
            {
                new Task() { Action = logHeartbeat, MinInterval = TimeSpan.FromMinutes(5) },
                new Task() { Action = checkAndProcessFileChanges, MinInterval = checkInterval },
            };
        }

        private void logHeartbeat()
        {
            lock (PropellerProgram.Log)
                PropellerProgram.Log.Debug("Heartbeat");
        }

        private static string configPath { get { return SettingsUtil.GetAttribute<PropellerSettings>().GetFileName(); } }

        private void checkAndProcessFileChanges()
        {
            bool mustReinitServer = false;

            if (_firstRunEver || !File.Exists(configPath) || _currentConfig == null || _configFileChangeTime < File.GetLastWriteTimeUtc(configPath))
            {
                mustReinitServer = true;
                refreshConfig();
            }

            if (!Directory.Exists(_currentConfig.PluginDirectoryExpanded))
            {
                try { Directory.CreateDirectory(_currentConfig.PluginDirectoryExpanded); }
                catch (Exception e)
                {
                    lock (PropellerProgram.Log)
                    {
                        PropellerProgram.Log.Error(e.Message);
                        PropellerProgram.Log.Error("Directory {0} cannot be created. Make sure the location is writable and try again, or edit the config file to change the path.".Fmt(_currentConfig.PluginDirectoryExpanded));
                    }
                    PropellerProgram.Service.Shutdown();
                    return;
                }
            }

            // Check whether any of the plugins reports that they need to be reinitialised.
            if (!mustReinitServer && _activeAppDomain.Runner != null)
                mustReinitServer = _activeAppDomain.Runner.MustReinitServer();

            if (mustReinitServer)
                reinitServer();

            _firstRunEver = false;

            var newInactiveDomains = new List<AppDomainInfo>();
            foreach (var entry in _inactiveAppDomains)
            {
                if (entry.Runner.ActiveHandlers == 0)
                {
                    entry.Runner.Shutdown();
                    AppDomain.Unload(entry.AppDomain);
                }
                else
                    newInactiveDomains.Add(entry);
            }
            _inactiveAppDomains = newInactiveDomains;
        }

        private void refreshConfig()
        {
            // Read configuration file
            _configFileChangeTime = new FileInfo(configPath).LastWriteTimeUtc;
            var newConfig = PropellerStandalone.LoadSettings(PropellerProgram.Log, _firstRunEver);
            _configFileChangeTime = new FileInfo(configPath).LastWriteTimeUtc; // a new file may have been created

            // If port number is different from previous port number, create a new listening thread and kill the old one
            if (_firstRunEver || _currentConfig == null || (newConfig.ServerOptions.Port != _currentConfig.ServerOptions.Port))
            {
                if (!_firstRunEver)
                    lock (PropellerProgram.Log)
                        PropellerProgram.Log.Info("Switching from port {0} to port {1}.".Fmt(_currentConfig.ServerOptions.Port, newConfig.ServerOptions.Port));
                if (_currentListeningThread != null)
                    _currentListeningThread.RequestExit();
                _currentListeningThread = new ListeningThread(this, newConfig.ServerOptions.Port.Value);
            }

            _currentConfig = newConfig;
        }

        private void reinitServer()
        {
            lock (PropellerProgram.Log)
                PropellerProgram.Log.Info(_firstRunEver ? "Starting Propeller..." : "Restarting Propeller...");

            lock (PropellerProgram.Log)
                PropellerProgram.Log.ConfigureVerbosity(_currentConfig.LogVerbosity);

            // Try to clean up old folders we've created before
            var tempPath = _currentConfig.TempDirectory ?? Path.GetTempPath();
            Directory.CreateDirectory(tempPath);
            foreach (var pth in Directory.GetDirectories(tempPath, "propeller-tmp-*"))
            {
                try { Directory.Delete(pth, true); }
                catch { }
            }

            // Find a new folder to put the DLL files into
            int j = 1;
            var copyToPath = Path.Combine(tempPath, "propeller-tmp-" + j);
            while (Directory.Exists(copyToPath))
            {
                j++;
                copyToPath = Path.Combine(tempPath, "propeller-tmp-" + j);
            }
            Directory.CreateDirectory(copyToPath);

            AppDomain newAppDomain = AppDomain.CreateDomain("Propeller AppDomainRunner " + (_appDomainCount++), null, new AppDomainSetup
            {
                ApplicationBase = PathUtil.AppPath,
                PrivateBinPath = copyToPath,
            });
            AppDomainRunner newRunner = (AppDomainRunner) newAppDomain.CreateInstanceAndUnwrap("Propeller", "Propeller.AppDomainRunner");

            lock (PropellerProgram.Log)
                newRunner.Init(_currentConfig.ServerOptions, _currentConfig.PluginDirectoryExpanded, copyToPath, PropellerProgram.Log);

            lock (_lockObject)
            {
                if (_activeAppDomain != null)
                    _inactiveAppDomains.Add(_activeAppDomain);
                _activeAppDomain = new AppDomainInfo { AppDomain = newAppDomain, Runner = newRunner };
            }

            lock (PropellerProgram.Log)
                PropellerProgram.Log.Info("Propeller initialisation successful.");
        }

        public override bool Shutdown(bool waitForExit)
        {
            if (_currentListeningThread != null)
            {
                _currentListeningThread.RequestExit(); ;
                if (waitForExit)
                    _currentListeningThread.WaitExited();
            }

            return base.Shutdown(waitForExit);
        }

        private sealed class ListeningThread
        {
            private Thread _listeningThread;
            private PropellerEngine _super;
            private int _port;
            private CancellationTokenSource _cancel = new CancellationTokenSource();
            private ManualResetEventSlim _exited = new ManualResetEventSlim();

            public ListeningThread(PropellerEngine super, int port)
            {
                _super = super;
                _port = port;
                _listeningThread = new Thread(listeningThreadFunction);
                _listeningThread.Start();
            }

            public void WaitExited()
            {
                _exited.Wait();
            }

            public void RequestExit()
            {
                _cancel.Cancel();
            }

            private void listeningThreadFunction()
            {
                lock (PropellerProgram.Log)
                    PropellerProgram.Log.Info("Start listening on port " + _port);
                TcpListener listener = new TcpListener(IPAddress.Any, _port);
                try
                {
                    listener.Start();
                }
                catch (SocketException e)
                {
                    lock (PropellerProgram.Log)
                        PropellerProgram.Log.Error("Cannot bind to port {0}: {1}".Fmt(_port, e.Message));
                    _exited.Set();
                    return;
                }
                try
                {
                    while (!_cancel.IsCancellationRequested)
                    {
                        if (listener.Pending())
                            listener.BeginAcceptSocket(acceptConnection, listener);
                        else
                            Thread.Sleep(1);
                    }
                }
                finally
                {
                    lock (PropellerProgram.Log)
                        PropellerProgram.Log.Info("Stop listening on port " + _port);
                    try { listener.Stop(); }
                    catch { }
                    _exited.Set();
                }
            }

            private void acceptConnection(IAsyncResult res)
            {
                var listener = (TcpListener) res.AsyncState;
                Socket sck = listener.EndAcceptSocket(res);
                lock (_super._lockObject)
                {
                    try
                    {
                        if (_super._activeAppDomain.Runner != null)
                            // This creates a new thread for handling the connection and returns pretty immediately.
                            _super._activeAppDomain.Runner.HandleRequest(sck.DuplicateAndClose(Process.GetCurrentProcess().Id));
                        else
                            sck.Close();
                    }
                    catch { }
                }
            }
        }
    }
}
