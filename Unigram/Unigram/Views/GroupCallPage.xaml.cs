﻿using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Td.Api;
using Unigram.Collections;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Converters;
using Unigram.Native.Calls;
using Unigram.Services;
using Unigram.ViewModels.Delegates;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace Unigram.Views
{
    public sealed partial class GroupCallPage : Page, IDisposable, IGroupCallDelegate
    {
        private readonly IProtoService _protoService;
        private readonly ICacheService _cacheService;
        private readonly IEventAggregator _aggregator;

        private readonly IGroupCallService _service;

        private VoipGroupManager _manager;
        private bool _disposed;

        private readonly ButtonWavesDrawable _drawable = new ButtonWavesDrawable();

        public GroupCallPage(IProtoService protoService, ICacheService cacheService, IEventAggregator aggregator, IGroupCallService voipService)
        {
            InitializeComponent();

            _protoService = protoService;
            _cacheService = cacheService;
            _aggregator = aggregator;

            _service = voipService;

            UpdateNetworkState();

            var titleBar = ApplicationView.GetForCurrentView().TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveForegroundColor = Colors.White;

            Window.Current.Closed += OnClosed;

            //Window.Current.SetTitleBar(BlurPanel);
            TitleInfo.Text = cacheService.GetTitle(voipService.Chat);
            PhotoInfo.Source = PlaceholderHelper.GetChat(protoService, voipService.Chat, 36);

            List.ItemsSource = new GroupCallParticipantsCollection(protoService, aggregator, voipService.Call.Id) { Delegate = this };

            if (voipService.Call != null)
            {
                Update(voipService.Call);
            }

            if (voipService.Manager != null)
            {
                Connect(voipService.Manager);
            }
        }

        private void OnClosed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            Window.Current.Content = null;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Dispose();

            AudioCanvas.RemoveFromVisualTree();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_manager != null)
            {
                _manager.NetworkStateUpdated -= OnNetworkStateUpdated;
                _manager.AudioLevelsUpdated -= OnAudioLevelsUpdated;
                _manager = null;
            }
        }

        public void Connect(VoipGroupManager controller)
        {
            if (_disposed)
            {
                return;
            }

            if (_manager != null)
            {
                // Let's avoid duplicated events
                _manager.NetworkStateUpdated -= OnNetworkStateUpdated;
                _manager.AudioLevelsUpdated -= OnAudioLevelsUpdated;
            }

            controller.NetworkStateUpdated += OnNetworkStateUpdated;
            controller.AudioLevelsUpdated += OnAudioLevelsUpdated;

            _manager = controller;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {

        }

        public void Update(GroupCall call)
        {
            if (_disposed)
            {
                return;
            }

            this.BeginOnUIThread(() =>
            {
                SubtitleInfo.Text = Locale.Declension("Participants", call.ParticipantCount);
                UpdateNetworkState(_service.IsConnected);
            });
        }

        private void OnNetworkStateUpdated(VoipGroupManager sender, bool newState)
        {
            this.BeginOnUIThread(() => UpdateNetworkState(newState));
        }

        private void OnAudioLevelsUpdated(VoipGroupManager sender, IReadOnlyDictionary<int, KeyValuePair<float, bool>> levels)
        {
            if (levels.TryGetValue(0, out var level))
            {
                _drawable.SetAmplitude(MathF.Max(0, MathF.Log(level.Key, short.MaxValue / 4000)));
            }
        }

        private async void Leave_Click(object sender, RoutedEventArgs e)
        {
            var chat = _service.Chat;
            var call = _service.Call;

            if (chat == null || call == null)
            {
                return;
            }

            if (call.CanBeManaged)
            {
                var dialog = new MessagePopup();
                dialog.RequestedTheme = ElementTheme.Dark;
                dialog.Title = Strings.Resources.VoipGroupLeaveAlertTitle;
                dialog.Message = Strings.Resources.VoipGroupLeaveAlertText;
                dialog.PrimaryButtonText = Strings.Resources.VoipGroupLeave;
                dialog.SecondaryButtonText = Strings.Resources.Cancel;
                dialog.CheckBoxLabel = Strings.Resources.VoipGroupLeaveAlertEndChat;

                var confirm = await dialog.ShowQueuedAsync();
                if (confirm == ContentDialogResult.Primary)
                {
                    Window.Current.Close();

                    if (dialog.IsChecked == true)
                    {
                        _service.Discard();
                    }
                    else
                    {
                        _service.Leave();
                    }
                }
            }
            else
            {
                Window.Current.Close();
                _service.Leave();
            }
        }

        private async void Discard_Click(object sender, RoutedEventArgs e)
        {
            var chat = _service.Chat;
            var call = _service.Call;

            if (chat == null || call == null)
            {
                return;
            }

            var dialog = new MessagePopup();
            dialog.RequestedTheme = ElementTheme.Dark;
            dialog.Title = Strings.Resources.VoipGroupEndAlertTitle;
            dialog.Message = Strings.Resources.VoipGroupEndAlertText;
            dialog.PrimaryButtonText = Strings.Resources.VoipGroupEnd;
            dialog.SecondaryButtonText = Strings.Resources.Cancel;

            var confirm = await dialog.ShowQueuedAsync();
            if (confirm == ContentDialogResult.Primary)
            {
                Window.Current.Close();
                _service.Discard();
            }
        }

        private void Audio_Click(object sender, RoutedEventArgs e)
        {
            if (_service.Manager != null)
            {
                _service.Manager.IsMuted = Audio.IsChecked == false;
                _protoService.Send(new ToggleGroupCallParticipantIsMuted(_service.Call.Id, _cacheService.Options.MyId, Audio.IsChecked == false));
            }

            UpdateNetworkState(_service.IsConnected);
        }

        private void UpdateNetworkState(bool? connected = null)
        {
            var call = _service.Call;
            if (call == null)
            {
                return;
            }

            if (connected == true)
            {
                Audio.IsEnabled = !(_service.Manager.IsMuted && !call.CanUnmuteSelf);
            }
            else if (connected == false)
            {
                Audio.IsEnabled = false;
            }

            if (Audio.IsEnabled)
            {
                if (Audio.IsChecked == true)
                {
                    _drawable.SetColors(Color.FromArgb(0xFF, 0x00, 0x78, 0xff), Color.FromArgb(0xFF, 0x33, 0xc6, 0x59));
                    Settings.Background = new SolidColorBrush { Color = Color.FromArgb(0x66, 0x33, 0xc6, 0x59) };
                }
                else
                {
                    _drawable.SetColors(Color.FromArgb(0xFF, 0x59, 0xc7, 0xf8), Color.FromArgb(0xFF, 0x00, 0x78, 0xff));
                    Settings.Background = new SolidColorBrush { Color = Color.FromArgb(0x66, 0x00, 0x78, 0xff) };
                }
            }
            else
            {
                _drawable.SetColors(Color.FromArgb(0xFF, 0x3e, 0x3f, 0x41), Color.FromArgb(0xFF, 0x3e, 0x3f, 0x41));
                Settings.Background = new SolidColorBrush { Color = Color.FromArgb(0x66, 0x3e, 0x3f, 0x41) };
            }
        }

        private async void Menu_ContextRequested(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var chat = _service.Chat;
            var call = _service.Call;

            if (chat == null || call == null)
            {
                return;
            }

            var supergroup = _cacheService.GetSupergroup(chat);
            if (supergroup == null)
            {
                return;
            }

            FontIcon GetCheckmark(bool condition)
            {
                return condition ? new FontIcon { Glyph = Icons.Checkmark } : null;
            }

            if (call.AllowedChangeMuteNewParticipants && supergroup != null && supergroup.CanManageVoiceChats())
            {
                flyout.CreateFlyoutItem(() => _protoService.Send(new ToggleGroupCallMuteNewParticipants(call.Id, false)), Strings.Resources.VoipGroupAllCanSpeak, GetCheckmark(!call.MuteNewParticipants));
                flyout.CreateFlyoutItem(() => _protoService.Send(new ToggleGroupCallMuteNewParticipants(call.Id, true)), Strings.Resources.VoipGroupOnlyAdminsCanSpeak, GetCheckmark(call.MuteNewParticipants));
            }

            flyout.CreateFlyoutSeparator();

            var inputId = _service.CurrentAudioInput;
            var outputId = _service.CurrentAudioOutput;

            var input = new MenuFlyoutSubItem();
            input.Text = "Microphone";
            input.Icon = new FontIcon { Glyph = Icons.MicOn, FontFamily = App.Current.Resources["TelegramThemeFontFamily"] as FontFamily };
            input.CreateFlyoutItem(id => _service.CurrentAudioInput = id, "", Strings.Resources.Default, GetCheckmark(inputId == ""));

            var inputDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            foreach (var device in inputDevices)
            {
                input.CreateFlyoutItem(id => _service.CurrentAudioInput = id, device.Id, device.Name, GetCheckmark(inputId == device.Id));
            }

            var output = new MenuFlyoutSubItem();
            output.Text = "Speaker";
            output.Icon = new FontIcon { Glyph = Icons.Speaker, FontFamily = App.Current.Resources["TelegramThemeFontFamily"] as FontFamily };
            output.CreateFlyoutItem(id => _service.CurrentAudioOutput = id, "", Strings.Resources.Default, GetCheckmark(outputId == ""));

            var outputDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
            foreach (var device in outputDevices)
            {
                output.CreateFlyoutItem(id => _service.CurrentAudioOutput = id, device.Id, device.Name, GetCheckmark(outputId == device.Id));
            }

            flyout.Items.Add(input);
            flyout.Items.Add(output);

            flyout.CreateFlyoutSeparator();
            flyout.CreateFlyoutItem(() => { }, Strings.Resources.VoipGroupShareInviteLink, new FontIcon { Glyph = Icons.Link });

            if (supergroup != null && supergroup.CanManageVoiceChats())
            {
                flyout.CreateFlyoutItem(() => Discard_Click(null, null), Strings.Resources.VoipGroupEndChat, new FontIcon { Glyph = Icons.Delete });
            }

            if (flyout.Items.Count > 0)
            {
                if (ApiInformation.IsEnumNamedValuePresent("Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode", "TopEdgeAlignedLeft"))
                {
                    flyout.Placement = FlyoutPlacementMode.TopEdgeAlignedLeft;
                }

                flyout.ShowAt((Button)sender);
            }
        }

        private void AudioCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            _drawable.Draw(sender, args.DrawingSession);
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TextListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += Member_ContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            var content = args.ItemContainer.ContentTemplateRoot as Grid;
            var participant = args.Item as GroupCallParticipant;

            UpdateGroupCallParticipant(content, participant);

            args.Handled = true;
        }

        public void UpdateGroupCallParticipant(GroupCallParticipant participant)
        {
            this.BeginOnUIThread(() =>
            {
                var container = List.ContainerFromItem(participant) as SelectorItem;
                var content = container?.ContentTemplateRoot as Grid;

                if (content == null)
                {
                    return;
                }

                UpdateGroupCallParticipant(content, participant);
            });
        }

        private void UpdateGroupCallParticipant(Grid content, GroupCallParticipant participant)
        {
            var user = _cacheService.GetUser(participant.UserId);
            if (user == null)
            {
                return;
            }

            var photo = content.Children[0] as ProfilePicture;
            var title = content.Children[1] as TextBlock;
            var speaking = content.Children[2] as TextBlock;
            var glyph = content.Children[3] as TextBlock;

            photo.Source = PlaceholderHelper.GetUser(_protoService, user, 36);

            title.Text = user.GetFullName();

            if (participant.IsMuted)
            {
                speaking.Text = Strings.Resources.Listening;
                speaking.Foreground = new SolidColorBrush { Color = Color.FromArgb(0xFF, 0x00, 0x78, 0xff) };
                glyph.Text = Icons.MicOff;
                glyph.Foreground = new SolidColorBrush { Color = participant.CanUnmuteSelf ? Color.FromArgb(0xFF, 0x85, 0x85, 0x85) : Colors.Red };
            }
            else if (participant.IsSpeaking)
            {
                speaking.Text = Strings.Resources.Speaking;
                speaking.Foreground = new SolidColorBrush { Color = Color.FromArgb(0xFF, 0x33, 0xc6, 0x59) };
                glyph.Text = Icons.MicOn;
                glyph.Foreground = new SolidColorBrush { Color = Color.FromArgb(0xFF, 0x33, 0xc6, 0x59) };
            }
            else
            {
                speaking.Text = Strings.Resources.Listening;
                speaking.Foreground = new SolidColorBrush { Color = Color.FromArgb(0xFF, 0x00, 0x78, 0xff) };
                glyph.Text = Icons.MicOn;
                glyph.Foreground = new SolidColorBrush { Color = Color.FromArgb(0xFF, 0x85, 0x85, 0x85) };
            }
        }

        private void Member_ContextRequested(UIElement sender, Windows.UI.Xaml.Input.ContextRequestedEventArgs args)
        {
            var flyout = new MenuFlyout();

            var element = sender as FrameworkElement;
            var participant = List.ItemFromContainer(sender) as GroupCallParticipant;

            var supergroup = _cacheService.GetSupergroup(_service.Chat);
            if (supergroup == null)
            {
                return;
            }

            if (supergroup != null && supergroup.CanManageVoiceChats() && participant.UserId != _cacheService.Options.MyId)
            {
                if (participant.IsMuted && !participant.CanUnmuteSelf)
                {
                    flyout.CreateFlyoutItem(() => _protoService.Send(new ToggleGroupCallParticipantIsMuted(_service.Call.Id, participant.UserId, participant.CanUnmuteSelf)), Strings.Resources.VoipGroupAllowToSpeak, new FontIcon { Glyph = Icons.MicOn });
                }
                else
                {
                    flyout.CreateFlyoutItem(() => _protoService.Send(new ToggleGroupCallParticipantIsMuted(_service.Call.Id, participant.UserId, participant.CanUnmuteSelf)), Strings.Resources.VoipGroupMute, new FontIcon { Glyph = Icons.MicOff });
                }
            }

            args.ShowAt(flyout, element);
        }
    }

    public class ButtonWavesDrawable
    {
        //private BlobDrawable buttonWaveDrawable = new BlobDrawable(6);
        //private BlobDrawable tinyWaveDrawable = new BlobDrawable(9);
        //private BlobDrawable bigWaveDrawable = new BlobDrawable(12);

        private BlobDrawable _buttonWaveDrawable = new BlobDrawable(3);
        private BlobDrawable _tinyWaveDrawable = new BlobDrawable(6);
        private BlobDrawable _bigWaveDrawable = new BlobDrawable(9);

        private float _amplitude;
        private float _animateAmplitudeDiff;
        private float _animateToAmplitude;

        private Color _stopStart;
        private Color _stopEnd;

        public ButtonWavesDrawable()
        {
            //this.buttonWaveDrawable.minRadius = 57.0f;
            //this.buttonWaveDrawable.maxRadius = 63.0f;
            //this.buttonWaveDrawable.generateBlob();
            //this.tinyWaveDrawable.minRadius = 62.0f;
            //this.tinyWaveDrawable.maxRadius = 72.0f;
            //this.tinyWaveDrawable.generateBlob();
            //this.bigWaveDrawable.minRadius = 65.0f;
            //this.bigWaveDrawable.maxRadius = 75.0f;
            //this.bigWaveDrawable.generateBlob();
            _buttonWaveDrawable.minRadius = 48.0f;
            _buttonWaveDrawable.maxRadius = 48.0f;
            _buttonWaveDrawable.GenerateBlob();
            _tinyWaveDrawable.minRadius = 50.0f;
            _tinyWaveDrawable.maxRadius = 50.0f;
            _tinyWaveDrawable.GenerateBlob();
            _bigWaveDrawable.minRadius = 52.0f;
            _bigWaveDrawable.maxRadius = 52.0f;
            _bigWaveDrawable.GenerateBlob();
            _tinyWaveDrawable.paint.A = 38;
            _bigWaveDrawable.paint.A = 76;
        }

        public void SetAmplitude(float amplitude)
        {
            _animateToAmplitude = amplitude;
            _animateAmplitudeDiff = (amplitude - _amplitude) / ((BlobDrawable.AMPLITUDE_SPEED * 500.0f) + 100.0f);
        }

        public void SetColors(Color start, Color end)
        {
            if (start == _stopStart && end == _stopEnd)
            {
                return;
            }

            _stopStart = start;
            _stopEnd = end;

            _layerGradient = null;
        }

        private int _lastUpdateTime;

        private CanvasRenderTarget _target;
        private CanvasRadialGradientBrush _layerGradient;
        private CanvasRadialGradientBrush _maskGradient;

        public void Draw(CanvasControl view, CanvasDrawingSession canvas)
        {
            int elapsedRealtime = Environment.TickCount;
            int access = elapsedRealtime - _lastUpdateTime;
            int unused = _lastUpdateTime = elapsedRealtime;
            if (access > 20)
            {
                access = 17;
            }
            long j = access;

            //this.tinyWaveDrawable.minRadius = 62.0f;
            //this.tinyWaveDrawable.maxRadius = 62.0f + (20.0f * BlobDrawable.FORM_SMALL_MAX);
            //this.bigWaveDrawable.minRadius = 65.0f;
            //this.bigWaveDrawable.maxRadius = 65.0f + (20.0f * BlobDrawable.FORM_BIG_MAX);
            //this.buttonWaveDrawable.minRadius = 57.0f;
            //this.buttonWaveDrawable.maxRadius = 57.0f + (12.0f * BlobDrawable.FORM_BUTTON_MAX);
            _tinyWaveDrawable.minRadius = 50.0f;
            _tinyWaveDrawable.maxRadius = 50.0f + (20.0f * BlobDrawable.FORM_SMALL_MAX);
            _bigWaveDrawable.minRadius = 52.0f;
            _bigWaveDrawable.maxRadius = 52.0f + (20.0f * BlobDrawable.FORM_BIG_MAX);
            _buttonWaveDrawable.minRadius = 48.0f;
            _buttonWaveDrawable.maxRadius = 48.0f + (12.0f * BlobDrawable.FORM_BUTTON_MAX);


            if (_animateToAmplitude != _amplitude)
            {
                _amplitude = _amplitude + (_animateAmplitudeDiff * ((float)j));

                if (_animateAmplitudeDiff > 0.0f)
                {
                    if (_amplitude > _animateToAmplitude)
                    {
                        _amplitude = _animateToAmplitude;
                    }
                }
                else if (_amplitude < _animateToAmplitude)
                {
                    _amplitude = _animateToAmplitude;
                }
                view.Invalidate();
            }

            _bigWaveDrawable.Update(_amplitude, 1.0f);
            _tinyWaveDrawable.Update(_amplitude, 1.0f);
            _buttonWaveDrawable.Update(_amplitude, 0.4f);

            if (_target == null)
            {
                _target = new CanvasRenderTarget(canvas, 300, 300);
            }

            using (var session = _target.CreateDrawingSession())
            {
                if (_maskGradient == null)
                {
                    _maskGradient = new CanvasRadialGradientBrush(session, Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
                    _maskGradient.Center = new Vector2(150, 150);
                }

                float bigScale = BlobDrawable.SCALE_BIG_MIN + (BlobDrawable.SCALE_BIG * _amplitude);
                float tinyScale = BlobDrawable.SCALE_SMALL_MIN + (BlobDrawable.SCALE_SMALL * _amplitude);
                float glowScale = bigScale * BlobDrawable.LIGHT_GRADIENT_SIZE + 0.7f;

                _maskGradient.RadiusX = 84 * glowScale;
                _maskGradient.RadiusY = 84 * glowScale;

                session.Clear(Colors.Transparent);
                session.FillEllipse(new Vector2(150, 150), 84 * glowScale, 84 * glowScale, _maskGradient);
                session.Transform = Matrix3x2.CreateScale(bigScale, new Vector2(150, 150));
                _bigWaveDrawable.Draw(session, 150, 150);
                session.Transform = Matrix3x2.CreateScale(tinyScale, new Vector2(150, 150));
                _tinyWaveDrawable.Draw(session, 150, 150);
                session.Transform = Matrix3x2.Identity;
                _buttonWaveDrawable.Draw(session, 150, 150);
            }
            using (var layer = canvas.CreateLayer(new CanvasImageBrush(canvas, _target)))
            {
                if (_layerGradient == null)
                {
                    _layerGradient = new CanvasRadialGradientBrush(canvas, _stopStart, _stopEnd);
                    _layerGradient.RadiusX = MathF.Sqrt(200 * 200 + 200 * 200);
                    _layerGradient.RadiusY = MathF.Sqrt(200 * 200 + 200 * 200);
                    _layerGradient.Center = new Vector2(300, 0);
                    //_layerGradient.RadiusX = 120;
                    //_layerGradient.RadiusY = 120;
                    //_layerGradient.Center = new Vector2(150 + 70, 150 - 70);
                }

                canvas.FillRectangle(0, 0, 300, 300, _layerGradient);
            }

            view.Invalidate();
        }
    }
}
