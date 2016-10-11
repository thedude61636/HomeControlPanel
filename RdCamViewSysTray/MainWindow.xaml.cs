﻿#define LISTEN_TO_CAMERA
#define TALK_TO_CAMERA

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using NAudio.Wave;
using System.IO;
using System.Net;
using System.Net.Sockets;
using MahApps.Metro.Controls;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Threading;
using MjpegProcessor;
using System.Net.Http;
using NLog;
using Newtonsoft.Json;

namespace RdWebCamSysTrayApp
{


    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        private MjpegDecoder _mjpeg1;
        private MjpegDecoder _mjpeg2;
        private MjpegDecoder _mjpeg3;
        //private MjpegDecoder _mjpeg4;
        private int rotationAngle = 0;
        bool listenToCameraOnShow = false;
#if (TALK_TO_CAMERA)
        private TalkToAxisCamera talkToAxisCamera;
#endif
#if (LISTEN_TO_CAMERA)
        private ListenToAxisCamera listenToAxisCamera;
#endif
        private const string frontDoorCameraIPAddress = "192.168.0.210";
        private const string secondCameraIPAddress = "192.168.0.211";
        private const string thirdCameraIPAddress = "192.168.0.213";
        private const string fourthCameraIPAddress = "192.168.0.166";
        private const string frontDoorIPAddress = "192.168.0.221";
        private const string catDeterrentIPAddress = "192.168.0.135";
        private const string officeBlindsIPAddress = "192.168.0.220";
        private const string ledMatrixIpAddress = "192.168.0.229";
        private List<string> domoticzIPAddresses = new List<string>(new string[] { "192.168.0.232", "192.168.0.233", "192.168.0.234", "192.168.0.235", "192.168.0.236" });
        private const string configFileSource = "//macallan/main/RobDev/ITConfig/RdCamViewSysTray.txt";
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        private UdpClient _udpClientForCameraMovement;
        private FrontDoorControl _frontDoorControl;
        private AudioDevices _localAudioDevices;
        private int _timeToListenAfterDoorbellRingInSecs = 300;
        private System.Windows.Controls.Control ControlToReceiveFocus;
        private EasyButtonImage doorLockedImages;
        private EasyButtonImage doorClosedImages;
        private EasyButtonImage doorBellImages;
        private static Logger logger = LogManager.GetCurrentClassLogger();

        private const int _cameraMovementNotifyPort = 1010;
        private const int _catDeterrentUDPPort = 7191;
        private BlindsControl _officeBlindsControl;
        private DomoticzControl _domoticzControl;
        private LedMatrix _ledMatrix;
        private int _autoHideRequiredSecs = 0;
        private const int AUTO_HIDE_AFTER_AUTO_SHOW_SECS = 30;
        private const int AUTO_HIDE_AFTER_MANUAL_SHOW_SECS = 120;
        private const int DOOR_STATUS_REFRESH_SECS = 2;
        private DispatcherTimer _dTimer = new DispatcherTimer();

        public MainWindow()
        {
            InitializeComponent();

            // Log startup
            logger.Info("App Starting ...");

            // Position window
            Left = Screen.PrimaryScreen.WorkingArea.Width - Width;
            Top = Screen.PrimaryScreen.WorkingArea.Height - Height;
            ResizeMode = System.Windows.ResizeMode.CanResizeWithGrip;
            ControlToReceiveFocus = this.Settings;

            // Notify icon
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            Stream iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/res/door48x48.ico")).Stream;
            _notifyIcon.Icon = new System.Drawing.Icon(iconStream);
            _notifyIcon.Visible = true;
            _notifyIcon.MouseUp +=
                new System.Windows.Forms.MouseEventHandler(delegate(object sender, System.Windows.Forms.MouseEventArgs args)
                {
                    if (args.Button == MouseButtons.Left)
                    {
                        if (!this.IsVisible)
                            ShowPopupWindow(AUTO_HIDE_AFTER_MANUAL_SHOW_SECS);
                        else
                            HidePopupWindow();
                    }
                    else
                    {
                        System.Windows.Forms.ContextMenu cm = new System.Windows.Forms.ContextMenu();
                        cm.MenuItems.Add("Exit...", new System.EventHandler(ExitApp));
                        _notifyIcon.ContextMenu = cm;
                        MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                        mi.Invoke(_notifyIcon, null);
                    }
                });

            try
            {
                _udpClientForCameraMovement = new UdpClient(_cameraMovementNotifyPort);
                _udpClientForCameraMovement.BeginReceive(new AsyncCallback(CameraMovementCallback), null);
                logger.Info("Socket bound to camera movement port {0}", _cameraMovementNotifyPort);
            }
            catch (SocketException excp)
            {
                logger.Error("Socket failed to bind to camera movement port {1} ({0})", excp.ToString(), _cameraMovementNotifyPort);
            }
            catch (Exception excp)
            {
                logger.Error("Other failed to bind to camera movement port {1} ({0})", excp.ToString(), _cameraMovementNotifyPort);
            }


            // Front door
            _frontDoorControl = new FrontDoorControl(frontDoorIPAddress, DoorStatusRefresh);

            // Office blinds
            _officeBlindsControl = new BlindsControl(officeBlindsIPAddress);

            // Domoticz
            _domoticzControl = new DomoticzControl(domoticzIPAddresses);

            // LedMatrix
            _ledMatrix = new LedMatrix(ledMatrixIpAddress);

            // Create the video decoder
            _mjpeg1 = new MjpegDecoder();
            _mjpeg1.FrameReady += mjpeg1_FrameReady;
            _mjpeg2 = new MjpegDecoder();
            _mjpeg2.FrameReady += mjpeg2_FrameReady;
            _mjpeg3 = new MjpegDecoder();
            _mjpeg3.FrameReady += mjpeg3_FrameReady;
            //_mjpeg4 = new MjpegDecoder();
            //_mjpeg4.FrameReady += mjpeg4_FrameReady;

            // Volume control
            _localAudioDevices = new AudioDevices();
            _localAudioDevices.SetOutVolumeWhenListening((float)Properties.Settings.Default.SpkrVol);
            outSlider.Value = Properties.Settings.Default.SpkrVol * 100;
            _localAudioDevices.SetInVolumeWhenTalking((float)Properties.Settings.Default.MicVol);
            inSlider.Value = Properties.Settings.Default.MicVol * 100;

            // Audio in/out
            string username = "";
            string password = "";
            try
            {
                string[] lines = File.ReadAllLines(configFileSource);
                username = lines[0];
                password = lines[1];
            }
            catch (Exception excp)
            {
                logger.Error("Cannot read username and password for Axis camera from " + configFileSource + " excp " + excp.ToString());
            }
#if (TALK_TO_CAMERA)
            talkToAxisCamera = new TalkToAxisCamera(frontDoorCameraIPAddress, 80, username, password, _localAudioDevices);
#endif
#if (LISTEN_TO_CAMERA)
            listenToAxisCamera = new ListenToAxisCamera(frontDoorCameraIPAddress, _localAudioDevices);
#endif
            // Start Video
            StartVideo();

            // Door status images
            doorLockedImages = new EasyButtonImage(@"res/locked-large.png", @"res/unlocked-large.png");
            doorClosedImages = new EasyButtonImage(@"res/doorclosed-large.png", @"res/dooropen-large.png");
            doorBellImages = new EasyButtonImage(@"res/doorbell-large-sq.png", @"res/doorbell-large.png");

            // Start getting updates from front door
            _frontDoorControl.StartUpdates();

            // Start update timer for status
            _dTimer.Tick += new EventHandler(dtimer_Tick);
            _dTimer.Interval = new TimeSpan(0, 0, 1);
            _dTimer.Start();

            // Log startup
            logger.Info("App Started");
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _notifyIcon.Visible = false;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            Properties.Settings.Default.Save();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                HidePopupWindow();
            }

            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;

            base.OnStateChanged(e);
        }

        public void BringWindowToFront()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
            this.Focus();
        }

        public void ShowPopupWindow(int autoHideSecs)
        {
            _autoHideRequiredSecs = autoHideSecs;
            BringWindowToFront();
            StartVideo();
#if (LISTEN_TO_CAMERA)
            if (this.listenToCameraOnShow)
                listenToAxisCamera.Start();
#endif
            logger.Info("Popup Shown");
        }

        public void HidePopupWindow()
        {
            logger.Info("Popup Hidden");
            StopVideo();
            StopTalkAndListen();
            this.Hide();
        }

        public void ExitApp(object sender, EventArgs e)
        {
            HidePopupWindow();
            _dTimer.Stop();
            StopVideo(true);
            StopTalkAndListen();
            if (System.Windows.Application.Current != null)
                System.Windows.Application.Current.Shutdown();
        }

        private void StartVideo()
        {
            _mjpeg1.ParseStream(new Uri("http://" + frontDoorCameraIPAddress + "/axis-cgi/mjpg/video.cgi"));
            _mjpeg2.ParseStream(new Uri("http://" + secondCameraIPAddress + "/img/video.mjpeg"));
            _mjpeg3.ParseStream(new Uri("http://" + thirdCameraIPAddress + "/img/video.mjpeg"));
            //_mjpeg4.ParseStream(new Uri("http://" + fourthCameraIPAddress + "/axis-cgi/mjpg/video.cgi"));
        }

        private void StopVideo(bool unsubscribeEvents = false)
        {
            _mjpeg1.StopStream();
            _mjpeg2.StopStream();
            _mjpeg3.StopStream();
            //_mjpeg4.StopStream();
            if (unsubscribeEvents)
            {
                _mjpeg1.FrameReady -= mjpeg1_FrameReady;
                _mjpeg2.FrameReady -= mjpeg2_FrameReady;
                _mjpeg3.FrameReady -= mjpeg3_FrameReady;
                //_mjpeg4.FrameReady -= mjpeg4_FrameReady;
            }
        }

        private void mjpeg1_FrameReady(object sender, FrameReadyEventArgs e)
        {
            if (rotationAngle != 0)
            {
                TransformedBitmap tmpImage = new TransformedBitmap();

                tmpImage.BeginInit();
                tmpImage.Source = e.BitmapImage; // of type BitmapImage

                RotateTransform transform = new RotateTransform(rotationAngle);
                tmpImage.Transform = transform;
                tmpImage.EndInit();

                image1.Source = tmpImage;
            }
            else
            {
                image1.Source = e.BitmapImage;
            }
        }

        private void mjpeg2_FrameReady(object sender, FrameReadyEventArgs e)
        {
            image2.Source = e.BitmapImage;
        }

        private void mjpeg3_FrameReady(object sender, FrameReadyEventArgs e)
        {
            image3.Source = e.BitmapImage;
        }

        //private void mjpeg4_FrameReady(object sender, FrameReadyEventArgs e)
        //{
        //    Int32Rect cropRect = new Int32Rect(400, 380, 300, 200);
        //    BitmapSource croppedImage = new CroppedBitmap(e.BitmapImage, cropRect);
        //    image4.Source = croppedImage;
        //}

        private void StartListen_Click(object sender, RoutedEventArgs e)
        {
#if (LISTEN_TO_CAMERA)
            if (!listenToAxisCamera.IsListening())
                listenToAxisCamera.Start();
#endif
            ControlToReceiveFocus.Focus();

        }

        private void StopListen_Click(object sender, RoutedEventArgs e)
        {
#if (LISTEN_TO_CAMERA)
            if (listenToAxisCamera.IsListening())
                listenToAxisCamera.Stop();
#endif
            ControlToReceiveFocus.Focus();

        }

        private void StopTalkAndListen()
        {
#if (TALK_TO_CAMERA)
            talkToAxisCamera.StopTalk();
#endif
#if (LISTEN_TO_CAMERA)
            listenToAxisCamera.Stop();
#endif
        }

        private void DoorStatusRefresh()
        {
            // Update the door status using a delegate as it is UI update
            this.Dispatcher.BeginInvoke(
                (Action)delegate ()
                    {
                        ShowDoorStatus();
                    }
                );
            // Check if popup window and start listing to audio from camera
            if (_frontDoorControl.IsDoorbellPressed())
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    (System.Windows.Forms.MethodInvoker)delegate ()
                        {
                            ShowPopupWindow(AUTO_HIDE_AFTER_AUTO_SHOW_SECS);
#if (LISTEN_TO_CAMERA)
                            listenToAxisCamera.ListenForAFixedPeriod(_timeToListenAfterDoorbellRingInSecs);
#endif
                        });
            }
        }

        private void CameraMovementCallback(IAsyncResult ar)
        {
            logger.Info("Camera movement {0}", ar.ToString());

            try
            {
                // Restart receive
                _udpClientForCameraMovement.BeginReceive(new AsyncCallback(CameraMovementCallback), null);

                System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                        (System.Windows.Forms.MethodInvoker)delegate ()
                        {
                            ShowPopupWindow(AUTO_HIDE_AFTER_AUTO_SHOW_SECS);
                        });
            }
            catch (Exception excp)
            {
                logger.Error("Exception in MainWindow::CameraMovementCallback2 {0}", excp.Message);
            }
        }

        private void Unlock_Main_Click(object sender, RoutedEventArgs e)
        {
            _frontDoorControl.UnlockMainDoor();
            ControlToReceiveFocus.Focus();
        }

        private void Lock_Main_Click(object sender, RoutedEventArgs e)
        {
            _frontDoorControl.LockMainDoor();
            ControlToReceiveFocus.Focus();
        }

        private void Unlock_Inner_Click(object sender, RoutedEventArgs e)
        {
            _frontDoorControl.UnlockInnerDoor();
            ControlToReceiveFocus.Focus();
        }

        private void Lock_Inner_Click(object sender, RoutedEventArgs e)
        {
            _frontDoorControl.LockInnerDoor();
            ControlToReceiveFocus.Focus();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            ControlToReceiveFocus.Focus();
            SettingsWindow sw = new SettingsWindow(_localAudioDevices);
            sw.Show();
        }

        private void outSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            float ov = (float)(e.NewValue / 100);
            _localAudioDevices.SetOutVolumeWhenListening(ov);
            Properties.Settings.Default.SpkrVol = ov;
        }

        private void inSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            float iv = (float)(e.NewValue / 100);
            _localAudioDevices.SetInVolumeWhenTalking(iv);
            Properties.Settings.Default.MicVol = iv;
        }


        private void TalkButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
#if (TALK_TO_CAMERA)
            if (!talkToAxisCamera.IsTalking())
            {
                // TalkButton.Background = System.Windows.Media.Brushes.Red;
                talkToAxisCamera.StartTalk();
            }
#endif
        }

        private void TalkButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
#if (TALK_TO_CAMERA)
            if (talkToAxisCamera.IsTalking())
            {
                talkToAxisCamera.StopTalk();
            }
#endif
            ControlToReceiveFocus.Focus();
        }

        private void ShowDoorStatus()
        {
            FrontDoorControl.DoorStatus doorStatus;
            _frontDoorControl.GetDoorStatus(out doorStatus);
            if (doorStatus._mainLocked)
                mainDoorLockState.Source = doorLockedImages.Img1();
            else
                mainDoorLockState.Source = doorLockedImages.Img2();
            if (doorStatus._innerLocked)
                innerDoorLockState.Source = doorLockedImages.Img1();
            else
                innerDoorLockState.Source = doorLockedImages.Img2();
            if (!doorStatus._mainOpen)
                mainDoorOpenState.Source = doorClosedImages.Img1();
            else
                mainDoorOpenState.Source = doorClosedImages.Img2();
            if (doorStatus._bellPressed)
                doorBellState.Source = doorBellImages.Img1();
            else
                doorBellState.Source = null;
        }

        private void dtimer_Tick(object sender, EventArgs e)
        {
            // Check for auto-hide required
            if (_autoHideRequiredSecs > 0)
            {
                _autoHideRequiredSecs--;
                if (_autoHideRequiredSecs == 0)
                {
                    HidePopupWindow();
                }
            }

        }

        private void SendUDPSquirtMessage(string msg)
        {
            try
            {
                //IPAddress multicastaddress = IPAddress.Parse(catDeterrentIPAddress);
                //UdpClient udpclient = new UdpClient(41252, AddressFamily.InterNetwork); ;
                //udpclient.JoinMulticastGroup(multicastaddress);
                //IPEndPoint remoteep = new IPEndPoint(multicastaddress, _catDeterrentUDPPort);
                //byte[] send_buffer = Encoding.ASCII.GetBytes(msg);
                //udpclient.Send(send_buffer, send_buffer.Length, remoteep);

                Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                IPAddress serverAddr = IPAddress.Parse(catDeterrentIPAddress);
                IPEndPoint endPoint = new IPEndPoint(serverAddr, _catDeterrentUDPPort);
                byte[] send_buffer = Encoding.ASCII.GetBytes(msg);
                sock.SendTo(send_buffer, endPoint);

                //Uri uri = new Uri("http://" + catDeterrentIPAddress + "/control.cgi?squirt=1");
                //// Using WebClient as can't get HttpClient to not block
                //WebClient requester = new WebClient();
                //requester.OpenReadCompleted += new OpenReadCompletedEventHandler(web_req_completed);
                //requester.OpenReadAsync(uri);

                logger.Info("MainWindow::SquirtButton activated");
            }
            catch (HttpRequestException excp)
            {
                logger.Error("MainWindow::SquirtButton exception {0}", excp.Message);
            }
            ControlToReceiveFocus.Focus();
        }

        private void web_req_completed(object sender, OpenReadCompletedEventArgs e)
        {
            if (e.Error == null)
            {
                logger.Info("MainWindow::SquirtButton ok");
            }
            else
            {
                logger.Info("MainWindow::SquirtButton error {0}", e.Error.ToString());
            }
        }           

        private void RobsUpButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(0, "up");
            ControlToReceiveFocus.Focus();
        }

        private void LeftUpButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(1, "up");
            ControlToReceiveFocus.Focus();
        }

        private void LeftMidUpButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(2, "up");
            ControlToReceiveFocus.Focus();
        }

        private void RightMidUpButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(3, "up");
            ControlToReceiveFocus.Focus();
        }

        private void RightUpButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(4, "up");
            ControlToReceiveFocus.Focus();
        }

        private void RobsStopButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(0, "stop");
            ControlToReceiveFocus.Focus();
        }

        private void LeftStopButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(1, "stop");
            ControlToReceiveFocus.Focus();
        }

        private void LeftMidStopButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(2, "stop");
            ControlToReceiveFocus.Focus();
        }

        private void RightMidStopButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(3, "stop");
            ControlToReceiveFocus.Focus();
        }

        private void RightStopButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(4, "stop");
            ControlToReceiveFocus.Focus();
        }

        private void RobsDownButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(0, "down");
            ControlToReceiveFocus.Focus();
        }

        private void LeftDownButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(1, "down");
            ControlToReceiveFocus.Focus();
        }

        private void LeftMidDownButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(2, "down");
            ControlToReceiveFocus.Focus();
        }

        private void RightMidDownButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(3, "down");
            ControlToReceiveFocus.Focus();
        }

        private void RightDownButton_Click(object sender, RoutedEventArgs e)
        {
            _officeBlindsControl.ControlBlind(4, "down");
            ControlToReceiveFocus.Focus();
        }

        private void OfficeLightsMoodButton_Click(object sender, RoutedEventArgs e)
        {
            _domoticzControl.SendGroupCommand("Office - Mood");
        }

        private void OfficeLightsOffButton_Click(object sender, RoutedEventArgs e)
        {
            _domoticzControl.SendGroupCommand("Office - Off");
        }
        private void TextMatrixSendButton_Click(object sender, RoutedEventArgs e)
        {
            _ledMatrix.SendMessage(LEDMatrixText.Text);
        }
        private void TextMatrixStopAlertButton_Click(object sender, RoutedEventArgs e)
        {
            _ledMatrix.StopAlert();
        }
        private void TextMatrixClearButton_Click(object sender, RoutedEventArgs e)
        {
            _ledMatrix.Clear();
        }

        private void SquirtButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SendUDPSquirtMessage("1");
        }

        private void SquirtButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            SendUDPSquirtMessage("0");
        }
    }
}
