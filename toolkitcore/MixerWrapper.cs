﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using ToolkitCore.Models.Mixer;
using ToolkitCore.Utilities;
using Verse;

namespace ToolkitCore
{
    [StaticConstructorOnStartup]
    public static class MixerWrapper
    {
        static readonly string MixerApiBaseUrl = "https://mixer.com/api/v1/";

        public static int ChannelId { get; set; }

        public static int UserId { get; set; }

        static AuthKeyResponse AuthKeyResponse { get; set; }

        static WebClient WebClient { get; } = new WebClient();

        static MixerWebSocketClient WebSocket { get; set; }

        static MixerWrapper()
        {
            WebClient.Headers.Add("user-agent", "ToolkitCore Rimworld Mod / 1.2");
            WebClient.Headers.Add("authorization", "Bearer " + ToolkitCoreSettings.mixerAccessToken);
        }

        public static bool Connected()
        {
            if (WebSocket == null)
            {
                return false;
            }

            return WebSocket.IsConnected;
        }

        public static async void InitializeClient()
        {
            WebClient.Headers.Set("authorization", "Bearer " + ToolkitCoreSettings.mixerAccessToken);

            await GetChannelId();
            await GetAuthKey();

            if (AuthKeyResponse.authkey == null || AuthKeyResponse.authkey == string.Empty)
            {
                Log.Error("AuthKey is null, cannot continue");
                return;
            }

            StartWebSocket();
        }

        static async Task GetChannelId()
        {
            string json = await WebClient.DownloadStringTaskAsync(new Uri($"{MixerApiBaseUrl}channels/{ToolkitCoreSettings.mixerUsername}?fields=id,userid"));

            ChannelResponse channelResponse = JsonConvert.DeserializeObject<ChannelResponse>(json);

            ChannelId = channelResponse.id;
            UserId = channelResponse.userId;

            Log.Message($"Mixer Channel Id: {channelResponse.id}");
        }

        static async Task GetAuthKey()
        {
            Uri uri = new Uri($"{MixerApiBaseUrl}chats/{ChannelId}");

            foreach (var key in WebClient.Headers.Keys)
            {
                Log.Message($"Header: {key} - {WebClient.Headers.Get(key.ToString())}");
            }

            string json = await WebClient.DownloadStringTaskAsync(new Uri($"{MixerApiBaseUrl}chats/{ChannelId}"));

            Log.Message($"AuthKey Reponse: {json}");

            AuthKeyResponse response = JsonConvert.DeserializeObject<AuthKeyResponse>(json);

            Log.Message($"Mixer Auth Key: {response.authkey}");

            AuthKeyResponse = response;
        }

        static void StartWebSocket()
        {
            WebSocket = new MixerWebSocketClient
            {
                Url = AuthKeyResponse.endpoints[0]
            };

            WebSocket.SetAuthKey(AuthKeyResponse.authkey);

            Log.Message("Starting connectiong to Mixer WebSocket");

            if (WebSocket.StartMixerServer())
            {
                Log.Message("Connected to Mixer WebSocket");

                WebSocket.OnMessage += (sender, e) =>
                {
                    ParseMessage(e.Message);
                };
            }
            else
            {
                Log.Error("Failed to connect to Mixer WebSocket");
            }
        }

        static void ParseMessage(string message)
        {
            Log.Message($"Raw Message Received: {message}");

            MixerEvent mixerEvent = JsonConvert.DeserializeObject<MixerEvent>(message);

            if (mixerEvent.type == "event")
            {
                switch (mixerEvent.Event)
                {
                    case "WelcomeEvent":
                        AuthMethod authRequest = new AuthMethod(ChannelId, UserId, AuthKeyResponse.authkey);

                        string json = JsonConvert.SerializeObject(authRequest);

                        WebSocket.Send(json);
                        break;
                    case "ChatMessage":
                        ChatMessageEvent chatMessage = JsonConvert.DeserializeObject<ChatMessageEvent>(message);

                        if (Current.Game == null) return;

                        if (chatMessage.Message().StartsWith("!"))
                        {
                            ChatCommandEvent chatCommand = JsonConvert.DeserializeObject<ChatCommandEvent>(message);

                            foreach (CommandInterfaceBase receiver in Current.Game.components.OfType<CommandInterfaceBase>())
                            {
                                receiver.ParseCommand(chatCommand);
                            }
                        }
                        else
                        {
                            foreach (MessageInterfaceBase receiver in Current.Game.components.OfType<MessageInterfaceBase>())
                            {
                                receiver.ParseMessage(chatMessage);
                            }
                        }

                        break;
                }
            }
            else if (mixerEvent.type == "reply")
            {
                MixerReply mixerReply = JsonConvert.DeserializeObject<MixerReply>(message);

                switch (mixerReply.id)
                {
                    case 0:
                        MixerAuthReply mixerAuthReply = JsonConvert.DeserializeObject<MixerAuthReply>(message);

                        if (mixerAuthReply.data.authenticated)
                        {
                            Log.Message("Successfully authenticated in chat with Mixer");
                            SendTestMessage();
                        }
                        else
                        {
                            Log.Warning("Unable to autheticated in chat with Mixer");
                        }

                        break;
                }
            }
            else
            {
                Log.Warning("Unrecognized Mixer Response of type " + mixerEvent.type);
            }
        }

        static void SendTestMessage()
        {
            MsgMethod msgMethod = new MsgMethod("ToolkitCore has Connected to Chat.");

            string json = JsonConvert.SerializeObject(msgMethod);

            WebSocket.Send(json);
        }

        internal static void SendChatMessage(string message)
        {
            if (!Connected())
            {
                Log.Error("Cannot send Mixer message");
                return;
            }

            MsgMethod msgMethod = new MsgMethod(message);

            string json = JsonConvert.SerializeObject(msgMethod);

            WebSocket.Send(json);
        }
    }
}
