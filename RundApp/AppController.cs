using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RundApp.UserInterface;
using AppNetDotNet.ApiCalls;
using AppNetDotNet.Model;
using System.Collections.ObjectModel;
using SnarlConnector;

namespace RundApp
{
    public class AppController
    {
        public static AppController Current;
        public MainWindow mainwindow;

        // enter your app details below
        private string client_id = "Your one please...";
        private string redirect_uri = "http://www.li-ghun.de/oauth/";
        private string scope = "basic stream write_post follow messages files update_profile";

        public static string access_token { get; set; }
        public Token token { get; set; }
        public User user { get; set; }

        public ThreadSaveObservableCollection<Broadcast.Broadcast_Channel> channels { get; set; }
        public ThreadSaveObservableCollection<Broadcast.Broadcast_Message> messages { get; set; }
        private Streaming.UserStream user_stream { get; set; }
        private bool streaming_is_active { get; set; }

        public SnarlInterface snarlInterface;

        public static void Start()
        {
            // We'll vreate a Singleton to be available all the time regardless on which windows are open
            if (Current == null)
            {
                Current = new AppController();
            }
        }

        private AppController()
        {
            Current = this;

            // the authorization is done using an embedded Internet Explorer control - which is in quirks mode by default
            // so the library has a method to tell Windows this app is fine to use a real browser mode (you can choose which one - IE 9 in this case)
            Authorization.registerAppInRegistry(Authorization.registerBrowserEmulationValue.IE9Always, alsoCreateVshostEntry: true);

            #region Check for update and upgrade settings if so
            try
            {
                if (!Properties.Settings.Default.settings_updated)
                {
                    Properties.Settings.Default.Upgrade();
                    Properties.Settings.Default.settings_updated = true;
                }
            }
            catch
            {
                try
                {
                    Properties.Settings.Default.Reset();
                }
                catch { }
            }
            #endregion

            #region init Snarl

            snarlInterface = new SnarlInterface();
            snarlInterface.RegisterWithEvents("RUndApp", "RundApp", "", "jkhoiotg", IntPtr.Zero, null);
            snarlInterface.AddClass("Unknown channel", "Unknown channel");
            // would be clicks...            snarlInterface.CallbackEvent += new SnarlInterface.CallbackEventHandler(snarl_CallbackEvent);
            //snarlInterface.GlobalSnarlEvent += new SnarlInterface.GlobalEventHandler(snarl_GlobalSnarlEvent);

            #endregion

            // note: you must not store the access_token in plain text. Add an encryption here!
            if (string.IsNullOrWhiteSpace(Properties.Settings.Default.access_token))
            {
                authorize_account();
            }
            else
            {
                // the access token should be stored encrypted!
                // it is not here in this example...
                access_token = Properties.Settings.Default.access_token;
                check_access_token();
            }
        }

        ~AppController()
        {
            snarlInterface.Unregister();
        }

        #region login stuff
        private void authorize_account()
        {
            // will open a new window asking the user to login and authorize your app
            // btw: this window will log out the user first which is handy if you want to support multiple accounts...
            AppNetDotNet.Model.Authorization.clientSideFlow apnClientAuthProcess = new Authorization.clientSideFlow(client_id, redirect_uri, scope);
            apnClientAuthProcess.AuthSuccess += apnClientAuthProcess_AuthSuccess;
            apnClientAuthProcess.showAuthWindow();
        }

        private bool check_access_token()
        {
            if (!string.IsNullOrWhiteSpace(access_token))
            {
                Tuple<Token, ApiCallResponse> response = Tokens.get(access_token);
                if (response.Item2.success)
                {
                    token = response.Item1;
                    user = token.user;
                    Properties.Settings.Default.access_token = access_token;
                    Properties.Settings.Default.Save();
                    open_main_window();

                    return true;
                }
            }
            // not authorized successfully - retrying
            authorize_account();
            return false;
        }

        void apnClientAuthProcess_AuthSuccess(object sender, AppNetDotNet.AuthorizationWindow.AuthEventArgs e)
        {
            if (e != null)
            {
                if (e.success)
                {
                    access_token = e.accessToken;
                    check_access_token();
                }
            }
        }

        private void open_main_window()
        {
            channels = new ThreadSaveObservableCollection<Broadcast.Broadcast_Channel>();
            messages = new ThreadSaveObservableCollection<Broadcast.Broadcast_Message>();

            mainwindow = new MainWindow();
            mainwindow.Show();

            start_fetching();
        }
        #endregion

        private void start_fetching()
        {
            // initial fetch
            Tuple<List<Broadcast.Broadcast_Channel>, ApiCallResponse> channels_response = Broadcasts.getOfCurrentUser(access_token);
            if (channels_response.Item2.success)
            {
                foreach (Broadcast.Broadcast_Channel channel in channels_response.Item1)
                {
                    add_new_channel(channel);
                }
            }
            else
            {
                // no channels so far
            }

            foreach (Broadcast.Broadcast_Channel broadcast_channel in channels)
            {
                Tuple<List<Broadcast.Broadcast_Message>, ApiCallResponse> messages_response = Broadcasts.getMessagesInChannel(access_token, broadcast_channel.raw_base_channel.id);
                if (messages_response.Item2.success)
                {
                    foreach (Broadcast.Broadcast_Message message in messages_response.Item1)
                    {
                        messages.Add(message);
                    }
                }
            }

            // now let's start with real time fetching ("user streaming")
            start_streaming();
        }

        private void add_new_channel(Broadcast.Broadcast_Channel channel)
        {
            channels.Add(channel);
            snarlInterface.AddClass(channel.title, channel.title);
        }

        private void add_new_message(Broadcast.Broadcast_Message message, string channel_name = "Unknown channel")
        {
            messages.Add(message);
            snarlInterface.Notify(channel_name, message.headline, message.text, 10, message.image_url, null);
        }

        private void start_streaming()
        {
            if (!streaming_is_active)
            {
                StreamingOptions streamingOptions = new StreamingOptions();
                streamingOptions.include_annotations = true;
                streamingOptions.include_html = false;
                streamingOptions.include_marker = true;
                streamingOptions.include_channel_annotations = true;
                streamingOptions.include_message_annotations = true;
                streamingOptions.include_post_annotations = true;
                streamingOptions.include_user_annotations = true;

                user_stream = new Streaming.UserStream(access_token, streamingOptions);

                IAsyncResult asyncResult = user_stream.StartUserStream(
                    channelsCallback: channels_callback,
                    streamStoppedCallback: stream_stopped_callback);

                SubscriptionOptions subscriptionOptions = new SubscriptionOptions();
                subscriptionOptions.include_deleted = false;
                subscriptionOptions.include_incomplete = false;
                subscriptionOptions.include_muted = false;
                subscriptionOptions.include_private = true;
                subscriptionOptions.include_read = true;
                List<string> channel_types = new List<string>();
                channel_types.Add("net.app.core.broadcast");
                subscriptionOptions.channel_types = channel_types;

                user_stream.available_endpoints["Channels"].options = subscriptionOptions;
                user_stream.subscribe_to_endpoint(user_stream.available_endpoints["Channels"]);

                streaming_is_active = true;
            }

        }

        public void channels_callback(List<Message> messages_received, bool is_deleted = false)
        {
            if (messages_received != null)
            {
                foreach (Message message in messages_received)
                {
                    if (message == null)
                    {
                        continue;
                    }


                    if (!message.is_deleted)
                    {
                        string channel_name = "Unknown channel";
                        try
                        {
                            Broadcast.Broadcast_Channel channel = channels.Where(c => c.raw_base_channel.id == message.channel_id).First();
                            if (channel != null)
                            {
                                channel_name = channel.title;
                            }

                        }
                        catch
                        {
                            // newly added channel as it seems...
                            Channels.channelParameters parameter = new Channels.channelParameters();
                            parameter.include_annotations = true;
                            parameter.include_marker = true;
                            Tuple<Channel, ApiCallResponse> channel_response = Channels.get(access_token, message.channel_id, parameters: parameter);
                            if (channel_response.Item2.success)
                            {
                                Broadcast.Broadcast_Channel broadcast_channel = new Broadcast.Broadcast_Channel(channel_response.Item1);
                                add_new_channel(broadcast_channel);
                                channel_name = broadcast_channel.title;
                            }
                        }
                        
                        Broadcast.Broadcast_Message broadcast_message = new Broadcast.Broadcast_Message(message);
                        if (string.IsNullOrWhiteSpace(broadcast_message.headline) && string.IsNullOrWhiteSpace(broadcast_message.text))
                        {
                            return;
                        }
                        add_new_message(broadcast_message, channel_name:channel_name);
                        
                        
                        /* geting the channel
                                Channels.channelParameters parameter = new Channels.channelParameters();
                                parameter.include_annotations = true;
                                parameter.include_marker = true;
                                Tuple<Channel, ApiCallResponse> channel_response = Channels.get(this.accessToken, message.channel_id, parameters: parameter);
                                if (channel_response.Item2.success)
                                {
                                    IChapperCollection collection = this.addNewChannel(channel_response.Item1);
                                    if (collection != null)
                                    {
                                        collection.items.Add(item);
                                    }

                                } */
                    }
                }
            }
        }

        public void stream_stopped_callback(Streaming.StopReasons reason)
        {
            streaming_is_active = false;
        }
    }
}
