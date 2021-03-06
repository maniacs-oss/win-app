﻿/*
 * Copyright (c) 2020 Proton Technologies AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using Autofac;
using Caliburn.Micro;
using ProtonVPN.Account;
using ProtonVPN.BugReporting;
using ProtonVPN.BugReporting.Attachments;
using ProtonVPN.Common.Extensions;
using ProtonVPN.Common.Logging;
using ProtonVPN.Common.OS.Processes;
using ProtonVPN.Common.OS.Services;
using ProtonVPN.Common.Threading;
using ProtonVPN.Config;
using ProtonVPN.Core.Abstract;
using ProtonVPN.Core.Api.Handlers;
using ProtonVPN.Core.Auth;
using ProtonVPN.Core.Events;
using ProtonVPN.Core.Ioc;
using ProtonVPN.Core.Language;
using ProtonVPN.Core.Modals;
using ProtonVPN.Core.Network;
using ProtonVPN.Core.OS.Net;
using ProtonVPN.Core.Profiles;
using ProtonVPN.Core.Servers;
using ProtonVPN.Core.Service;
using ProtonVPN.Core.Service.Settings;
using ProtonVPN.Core.Service.Vpn;
using ProtonVPN.Core.Settings;
using ProtonVPN.Core.Settings.Migrations;
using ProtonVPN.Core.Startup;
using ProtonVPN.Core.Update;
using ProtonVPN.Core.User;
using ProtonVPN.Core.Vpn;
using ProtonVPN.FlashNotifications;
using ProtonVPN.Login.ViewModels;
using ProtonVPN.Login.Views;
using ProtonVPN.Map;
using ProtonVPN.Map.ViewModels;
using ProtonVPN.Onboarding;
using ProtonVPN.P2PDetection;
using ProtonVPN.QuickLaunch;
using ProtonVPN.Resources;
using ProtonVPN.Sidebar;
using ProtonVPN.Trial;
using ProtonVPN.ViewModels;
using ProtonVPN.Windows;
using Sentry;
using Sentry.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Windows;

namespace ProtonVPN.Core
{
    internal class Bootstrapper : BootstrapperBase
    {
        private IContainer _container;

        private T Resolve<T>() => _container.Resolve<T>();

        protected override void Configure()
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule<CoreModule>()
                .RegisterModule<UiModule>()
                .RegisterModule<AppModule>()
                .RegisterModule<BugReportingModule>()
                .RegisterModule<LoginModule>()
                .RegisterModule<P2PDetectionModule>()
                .RegisterModule<ProfilesModule>()
                .RegisterModule<TrialModule>();

            _container = builder.Build();
        }

        protected override async void OnStartup(object sender, StartupEventArgs e)
        {
            base.OnStartup(sender, e);

            Resolve<ServicePointConfiguration>().Apply();

            var appConfig = Resolve<Common.Configuration.Config>();

            Resolve<LogCleaner>().Clean(appConfig.AppLogFolder, 30);
            Resolve<ILogger>().Info($"= Booting ProtonVPN version: {appConfig.AppVersion} os: {Environment.OSVersion.VersionString} {appConfig.OsBits} bit =");
            LoadServersFromCache();

            RegisterMigrations(Resolve<AppSettingsStorage>(), Resolve<IEnumerable<IAppSettingsMigration>>());

            RegisterMigrations(Resolve<UserSettings>(), Resolve<IEnumerable<IUserSettingsMigration>>());
            Resolve<SyncedAutoStartup>().Sync();

            ShowInitialWindow();
            RegisterEvents();
            IncreaseAppStartCount();

            if (!LoggedInWithSavedCredentials() || !await IsUserValid() || await SessionExpired())
            {
                ShowLoginForm();
                return;
            }

            Resolve<UserAuth>().InvokeAutoLoginEvent();
        }

        public void OnExit()
        {
            var appSettings = Resolve<IAppSettings>();
            if (!appSettings.RememberLogin)
            {
                Resolve<IUserStorage>().ClearLogin();
                appSettings.LoggedInWithSavedCredentials = false;
            }

            Resolve<TrayIcon>().Hide();
            Resolve<VpnServiceWrapper>().Stop();
            Resolve<AppUpdateServiceWrapper>().Stop();
        }

        private async Task<bool> SessionExpired()
        {
            try
            {
                var result = await Resolve<UserAuth>().RefreshVpnInfo();
                return result.Failure;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        private void IncreaseAppStartCount()
        {
            Resolve<IAppSettings>().AppStartCounter++;
        }

        private void LoadServersFromCache()
        {
            var servers = Resolve<ServersFileStorage>().Get();
            if (servers.Count > 0)
                Resolve<ServerManager>().Load(servers);
        }

        private bool LoggedInWithSavedCredentials()
        {
            return Resolve<IAppSettings>().LoggedInWithSavedCredentials &&
                   !Resolve<IUserStorage>().User().Empty();
        }

        private async Task<bool> IsUserValid()
        {
            try
            {
                var validateResult = await Resolve<UserValidator>().GetValidateResult();
                if (validateResult.Failure)
                {
                    Resolve<LoginErrorViewModel>().SetDetailedError(validateResult.Error);
                    ShowLoginForm();
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                Resolve<LoginErrorViewModel>().SetDetailedError(ex.Message);
                ShowLoginForm();
                return false;
            }

            return true;
        }

        private void ShowInitialWindow()
        {
            var loginWindow = Resolve<LoginWindow>();
            var loginWindowViewModel = Resolve<LoginWindowViewModel>();
            Application.Current.MainWindow = loginWindow;
            loginWindowViewModel.CurrentPageViewModel = Resolve<LoadingViewModel>();
            loginWindow.DataContext = loginWindowViewModel;
            loginWindow.Show();
        }

        private void RegisterEvents()
        {
            var vpnServiceManager = Resolve<IVpnServiceManager>();
            var userAuth = Resolve<UserAuth>();
            var vpnManager = Resolve<VpnManager>();
            var appWindow = Resolve<AppWindow>();
            var appSettings = Resolve<IAppSettings>();

            Resolve<ServersUpdater>().ServersUpdated += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IServersAware>>();
                foreach (var instance in instances)
                {
                    instance.OnServersUpdated();
                }
            };

            Resolve<IUserLocationService>().UserLocationChanged += (sender, location) =>
            {
                var instances = Resolve<IEnumerable<IUserLocationAware>>();
                foreach (var instance in instances)
                {
                    instance.OnUserLocationChanged(location);
                }
            };

            userAuth.UserLoggingIn += (sender, e) => OnUserLoggingIn();

            userAuth.UserLoggedIn += async (sender, e) =>
            {
                var instances = Resolve<IEnumerable<ILoggedInAware>>();
                foreach (var instance in instances)
                {
                    instance.OnUserLoggedIn();
                }

                await SwitchToAppWindow(e.AutoLogin);
            };

            userAuth.UserLoggedOut += (sender, e) =>
            {
                Resolve<IModals>().CloseAll();
                SwitchToLoginWindow();
                Resolve<AppWindow>().Hide();
                var instances = Resolve<IEnumerable<ILogoutAware>>();
                foreach (var instance in instances)
                {
                    instance.OnUserLoggedOut();
                }

                Resolve<VpnServiceWrapper>().Stop();
                Resolve<AppUpdateServiceWrapper>().Stop();
            };

            Resolve<IUserStorage>().UserDataChanged += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IUserDataAware>>();
                foreach (var instance in instances)
                {
                    instance.OnUserDataChanged();
                }
            };

            Resolve<IUserStorage>().VpnPlanChanged += async (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IVpnPlanAware>>();
                foreach (var instance in instances)
                {
                    await instance.OnVpnPlanChangedAsync(e);
                }
            };

            Resolve<SyncProfiles>().SyncStatusChanged += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IProfileSyncStatusAware>>();
                foreach (var instance in instances)
                {
                    instance.OnProfileSyncStatusChanged(e.Status, e.ErrorMessage, e.ChangesSyncedAt);
                }
            };

            Resolve<PinFactory>().PinsChanged += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IPinChangeAware>>();
                foreach (var instance in instances)
                {
                    instance.OnPinsChanged();
                }
            };

            vpnServiceManager.RegisterCallback(vpnManager.OnVpnStateChanged);

            vpnManager.VpnStateChanged += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IVpnStateAware>>();
                foreach (var instance in instances)
                {
                    instance.OnVpnStateChanged(e);
                }

                Resolve<IEventAggregator>().PublishOnCurrentThread(e);
            };

            Resolve<UpdateService>().UpdateStateChanged += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IUpdateStateAware>>();
                foreach (var instance in instances)
                {
                    instance.OnUpdateStateChanged(e);
                }
            };

            Resolve<INetworkClient>().WifiChangeDetected += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IWifiChangeAware>>();
                foreach (var instance in instances)
                {
                    instance.OnWifiChangeDetected(e.Name, e.Secure);
                }
            };

            Resolve<P2PDetector>().TrafficForwarded += (sender, ip) =>
            {
                var instances = Resolve<IEnumerable<ITrafficForwardedAware>>();
                foreach (var instance in instances)
                {
                    instance.OnTrafficForwarded(ip);
                }
            };

            Resolve<SidebarManager>().ManualSidebarModeChangeRequested += appWindow.OnManualSidebarModeChangeRequested;

            appSettings.PropertyChanged += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<ISettingsAware>>();
                foreach (var instance in instances)
                {
                    instance.OnAppSettingsChanged(e);
                }
            };

            Resolve<Onboarding.Onboarding>().StepChanged += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IOnboardingStepAware>>();
                foreach (var instance in instances)
                {
                    instance.OnStepChanged(e);
                }
            };

            Resolve<TrialTimer>().TrialTimerTicked += (sender, e) =>
            {
                var instances = Resolve<IEnumerable<ITrialDurationAware>>();
                foreach (var instance in instances)
                {
                    instance.OnTrialSecondElapsed(e);
                }
            };

            Resolve<Trial.Trial>().StateChanged += async (sender, e) =>
            {
                var instances = Resolve<IEnumerable<ITrialStateAware>>();
                foreach (var instance in instances)
                {
                    await instance.OnTrialStateChangedAsync(e);
                }
            };

            Resolve<Language.Language>().LanguageChanged += (sender, lang) =>
            {
                var instances = Resolve<IEnumerable<ILanguageAware>>();
                foreach (var instance in instances)
                {
                    instance.OnLanguageChanged(lang);
                }
            };

            Resolve<EventClient>().ApiDataChanged += async (sender, e) =>
            {
                var instances = Resolve<IEnumerable<IApiDataChangeAware>>();
                foreach (var instance in instances)
                {
                    await instance.OnApiDataChanged(e);
                }
            };

            Resolve<Attachments>().OnErrorOccured += (sender, e) =>
            {
                Resolve<ReportBugModalViewModel>().OnAttachmentErrorOccured(e);
            };

            Resolve<UnauthorizedResponseHandler>().SessionExpired += (sender, e) =>
            {
                Resolve<IScheduler>().Schedule(async () =>
                {
                    await Resolve<UserAuth>().Logout();
                    Resolve<LoginViewModel>().OnSessionExpired();
                });
            };

            Resolve<OutdatedAppHandler>().ForcedLogout += (sender, e) =>
            {
                Resolve<IScheduler>().Schedule(async () =>
                {
                    await Resolve<UserAuth>().Logout();
                    Resolve<LoginViewModel>().OnForcedLogout(string.IsNullOrEmpty(e.Error)
                        ? StringResources.Get("Login_Error_msg_AppIsOutdated")
                        : e.Error);
                });
            };

            Resolve<MonitoredVpnService>().ServiceStartedHandler += async (sender, name) =>
            {
                await Resolve<IScheduler>().Schedule(async () =>
                {
                    await InitializeStateFromService();
                });

                await Resolve<SettingsServiceClientManager>().UpdateServiceSettings();
            };

            Resolve<IModals>();
        }

        private void OnUserLoggingIn()
        {
            Resolve<LoginWindowViewModel>().CurrentPageViewModel = Resolve<LoadingViewModel>();
        }

        private void SwitchToLoginWindow()
        {
            var loginWindowViewModel = Resolve<LoginWindowViewModel>();
            var loginWindow = Resolve<LoginWindow>();
            loginWindowViewModel.CurrentPageViewModel = Resolve<LoginViewModel>();
            loginWindow.DataContext = loginWindowViewModel;
            Application.Current.MainWindow = loginWindow;
            loginWindow.Show();
        }

        private void ShowLoginForm()
        {
            Resolve<LoginWindowViewModel>().CurrentPageViewModel = Resolve<LoginViewModel>();
        }

        private async Task SwitchToAppWindow(bool autoLogin)
        {
            var appConfig = Resolve<Common.Configuration.Config>();
            var serversUpdater = Resolve<ServersUpdater>();
            await serversUpdater.Update();

            if (!Resolve<UserAuth>().LoggedIn)
            {
                return;
            }

            Resolve<PinFactory>().BuildPins();

            LoadViewModels();
            await StartService(Resolve<MonitoredVpnService>());
            await StartService(Resolve<AppUpdateServiceWrapper>());
            Resolve<P2PDetector>();
            Resolve<VpnInfoChecker>().Start(appConfig.VpnInfoCheckInterval.RandomizedWithDeviation(0.2));

            var appWindow = Resolve<AppWindow>();
            appWindow.DataContext = Resolve<MainViewModel>();
            Application.Current.MainWindow = appWindow;
            appWindow.Show();
            Resolve<LoginWindow>().Hide();

            await Resolve<IUserLocationService>().Update();
            await Resolve<IVpnConfig>().Update();
            await Resolve<AutoConnect>().Load(autoLogin);
            await Resolve<Trial.Trial>().Load();
            Resolve<SyncProfiles>().Sync();
            Resolve<INetworkClient>().CheckForInsecureWiFi();
            await Resolve<EventClient>().StoreLatestEvent();
            Resolve<EventTimer>().Start();
        }

        private void LoadViewModels()
        {
            Resolve<MainViewModel>().Load();
            Resolve<CountriesViewModel>().Load();
            Resolve<QuickLaunchViewModel>().Load();
            Resolve<MapViewModel>().Load();
            Resolve<SidebarViewModel>().Load();
            Resolve<SidebarProfilesViewModel>().Load();
            Resolve<ConnectionStatusViewModel>().Load();
        }

        private async Task InitializeStateFromService()
        {
            try
            {
                await Resolve<VpnManager>().GetState();
            }
            catch (CommunicationException e)
            {
                Resolve<ILogger>().Error(e);
            }
            catch (TimeoutException e)
            {
                Resolve<ILogger>().Error(e);
            }
        }

        private async Task StartService(IService service)
        {
            var result = await service.StartAsync();

            if (result.Failure)
            {
                SentrySdk.WithScope(scope =>
                {
                    scope.Level = SentryLevel.Error;
                    scope.SetTag("captured_in", "App_Bootstrapper_StartService");
                    SentrySdk.CaptureException(result.Exception);
                });

                var config = Resolve<Common.Configuration.Config>();
                var filename = config.ErrorMessageExePath;
                var error = GetServiceErrorMessage(service, result.Exception);
                try
                {
                    Resolve<IOsProcesses>().Process(filename, error).Start();
                }
                catch (Exception e)
                {
                    var serviceName = Path.GetFileNameWithoutExtension(filename);
                    Resolve<ILogger>().Error($"Failed to start {serviceName} process: {e.CombinedMessage()}");
                    SentrySdk.WithScope(scope =>
                    {
                        scope.Level = SentryLevel.Error;
                        scope.SetTag("captured_in", "App_Bootstrapper_StartService");
                        SentrySdk.CaptureException(e);
                    });
                }
            }
        }

        private string GetServiceErrorMessage(IService service, Exception e)
        {
            var error = e.InnerException?.Message ?? e.Message;
            var failedToStart = string.Format(StringResources.Get("Dialogs_ServiceStart_msg_FailedToStart"), service.Name);

            return $"\"{failedToStart}\" \"{error}\"";
        }

        private void RegisterMigrations(ISupportsMigration subject, IEnumerable<IMigration> migrations)
        {
            foreach (var migration in migrations)
            {
                subject.RegisterMigration(migration);
            }
        }
    }
}
