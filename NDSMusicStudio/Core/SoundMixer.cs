﻿using Kermalis.NDSMusicStudio.UI;
using Kermalis.NDSMusicStudio.Util;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using System;
using System.Linq;

namespace Kermalis.NDSMusicStudio.Core
{
    class SoundMixer : IAudioSessionEventsHandler
    {
        public static SoundMixer Instance { get; } = new SoundMixer();

        public readonly bool[] Mutes;
        public Channel[] Channels;

        readonly BufferedWaveProvider buffer;
        readonly IWavePlayer @out;
        readonly AudioSessionControl appVolume;

        private SoundMixer()
        {
            Channels = new Channel[0x10];
            for (byte i = 0; i < 0x10; i++)
            {
                Channels[i] = new Channel(i);
            }

            Mutes = new bool[0x10];

            buffer = new BufferedWaveProvider(new WaveFormat(65456, 16, 2))
            {
                DiscardOnBufferOverflow = true,
                BufferLength = 0x5540
            };
            @out = new WasapiOut();
            @out.Init(buffer);
            SessionCollection sessions = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioSessionManager.Sessions;
            int id = System.Diagnostics.Process.GetCurrentProcess().Id;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl session = sessions[i];
                if (session.GetProcessID == id)
                {
                    appVolume = session;
                    appVolume.RegisterEventClient(this);
                    break;
                }
            }
            @out.Play();
        }

        public Channel AllocateChannel(InstrumentType type, Track track)
        {
            ushort allowedChannels;
            switch (type)
            {
                case InstrumentType.PCM: allowedChannels = 0b1111111111111111; break; // All channels
                case InstrumentType.PSG: allowedChannels = 0b0011111100000000; break; // Only 8 9 10 11 12 13
                case InstrumentType.Noise: allowedChannels = 0b1100000000000000; break; // Only 14 15
                default: return null;
            }
            Channel nChn = null;
            IOrderedEnumerable<Channel> byOwner = Channels.Where(c => (allowedChannels & (1 << c.Index)) != 0).OrderByDescending(c => c.Owner == null ? 0xFF : c.Owner.Index);
            foreach (Channel i in byOwner) // Find free
            {
                if (i.Owner == null)
                {
                    nChn = i;
                    break;
                }
            }
            if (nChn == null) // Find releasing
            {
                foreach (Channel i in byOwner)
                {
                    if (i.State == EnvelopeState.Release)
                    {
                        nChn = i;
                        break;
                    }
                }
            }
            if (nChn == null) // Find prioritized
            {
                foreach (Channel i in byOwner)
                {
                    if (track.Priority > i.Owner.Priority)
                    {
                        nChn = i;
                        break;
                    }
                }
            }
            if (nChn == null) // None available
            {
                Channel lowest = byOwner.First(); // Kill lowest track's instrument if the track is lower than this one
                if (lowest.Owner.Index >= track.Index)
                {
                    nChn = lowest;
                }
            }
            return nChn;
        }

        public void ChannelTick()
        {
            for (int i = 0; i < 0x10; i++)
            {
                Channel chan = Channels[i];
                if (chan.Owner != null)
                {
                    chan.StepEnvelope();
                    if (chan.NoteLength == 0 && !chan.Owner.WaitingForNoteToFinishBeforeContinuingXD)
                    {
                        chan.State = EnvelopeState.Release;
                    }
                    int vol = Utils.SustainTable[chan.NoteVelocity] + chan.Velocity + chan.Owner.GetVolume();
                    int pitch = ((chan.Key - chan.BaseKey) << 6) + chan.SweepMain() + chan.Owner.GetPitch(); // "<< 6" is "* 0x40"
                    if (chan.State != EnvelopeState.Release || vol > -92544)
                    {
                        chan.Volume = Utils.GetChannelVolume(vol);
                        chan.Pan = (sbyte)Utils.Clamp(chan.StartingPan + chan.Owner.GetPan(), -0x40, 0x3F);
                        chan.Timer = Utils.GetChannelTimer(chan.BaseTimer, pitch);
                    }
                    else // EnvelopeState.Dying
                    {
                        chan.Stop();
                    }
                }
            }
        }

        // Called 192 times a second
        public void Process()
        {
            for (int i = 0; i < 0x155; i++) // 0x155 (SamplesPerBuffer) == 0x5540/0x40
            {
                int left = 0, right = 0;
                for (int j = 0; j < 0x10; j++)
                {
                    Channel chan = Channels[j];
                    if (chan.Owner != null)
                    {
                        bool muted = Mutes[chan.Owner.Index];
                        chan.Process(out short channelLeft, out short channelRight);
                        if (!muted)
                        {
                            left += channelLeft;
                            right += channelRight;
                        }
                    }
                }
                left = Utils.Clamp(left, short.MinValue, short.MaxValue);
                right = Utils.Clamp(right, short.MinValue, short.MaxValue);
                // Convert two shorts to four bytes
                buffer.AddSamples(new byte[] { (byte)(left & 0xFF), (byte)((left >> 8) & 0xFF), (byte)(right & 0xFF), (byte)((right >> 8) & 0xFF) }, 0, 4);
            }
        }

        bool ignoreVolChangeFromUI = false;
        public void OnVolumeChanged(float volume, bool isMuted)
        {
            if (!ignoreVolChangeFromUI)
            {
                MainForm.Instance.SetVolumeBarValue(volume);
            }
            ignoreVolChangeFromUI = false;
        }
        public void OnDisplayNameChanged(string displayName)
        {
            throw new NotImplementedException();
        }
        public void OnIconPathChanged(string iconPath)
        {
            throw new NotImplementedException();
        }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex)
        {
            throw new NotImplementedException();
        }
        public void OnGroupingParamChanged(ref Guid groupingId)
        {
            throw new NotImplementedException();
        }
        // Fires on @out.Play() and @out.Stop()
        public void OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.AudioSessionStateActive)
            {
                OnVolumeChanged(appVolume.SimpleAudioVolume.Volume, appVolume.SimpleAudioVolume.Mute);
            }
        }
        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            throw new NotImplementedException();
        }
        public void SetVolume(float volume)
        {
            ignoreVolChangeFromUI = true;
            appVolume.SimpleAudioVolume.Volume = volume;
        }
    }
}
