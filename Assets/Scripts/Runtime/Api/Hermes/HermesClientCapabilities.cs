// HermesClientCapabilities.cs - DTOs and identity helpers for client.register

using System;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    [Serializable]
    public class ClientPlatformInfo
    {
        public string os;
        public string shell;
    }

    [Serializable]
    public class TerminalCapability
    {
        public int version = 1;
        public bool streaming;
        public bool cancel;
    }

    [Serializable]
    public class FileTransferCapability
    {
        public int version = 1;
        public int chunk_size_max = FileTransferProtocol.DefaultChunkSizeMax;
        public bool resume;
    }

    [Serializable]
    public class ClientCapabilitiesPayload
    {
        public TerminalCapability terminal;
        public FileTransferCapability file_transfer;
    }

    [Serializable]
    public class ClientRegisterParams
    {
        public string client_id;
        public string instance_id;
        public string name;
        public int protocol_version;
        public ClientPlatformInfo platform;
        public ClientCapabilitiesPayload capabilities;
    }

    [Serializable]
    public class ClientRegisterResult
    {
        public bool accepted;
        public int protocol_version;
    }

    /// <summary>
    /// Stable installation id (PlayerPrefs) and per-process instance id for client.register.
    /// </summary>
    public static class HermesClientIdentity
    {
        private const string ClientIdKey = "hermes.client_id";
        private static readonly string InstanceId = Guid.NewGuid().ToString();

        public static string ClientId
        {
            get
            {
                string stored = PlayerPrefs.GetString(ClientIdKey, string.Empty);
                if (!string.IsNullOrEmpty(stored))
                    return stored;

                string generated = Guid.NewGuid().ToString();
                PlayerPrefs.SetString(ClientIdKey, generated);
                PlayerPrefs.Save();
                return generated;
            }
        }

        public static string InstanceIdValue => InstanceId;

        public static ClientPlatformInfo DetectPlatform()
        {
            var info = new ClientPlatformInfo();
            RuntimePlatform platform = Application.platform;

            if (platform == RuntimePlatform.WindowsEditor || platform == RuntimePlatform.WindowsPlayer)
            {
                info.os = "windows";
                info.shell = "powershell";
            }
            else if (platform == RuntimePlatform.OSXEditor || platform == RuntimePlatform.OSXPlayer)
            {
                info.os = "macos";
                info.shell = "bash";
            }
            else if (platform == RuntimePlatform.LinuxEditor || platform == RuntimePlatform.LinuxPlayer)
            {
                info.os = "linux";
                info.shell = "bash";
            }
            else if (platform == RuntimePlatform.Android)
            {
                info.os = "android";
                info.shell = "sh";
            }
            else if (platform == RuntimePlatform.IPhonePlayer)
            {
                info.os = "ios";
                info.shell = "sh";
            }
            else
            {
                info.os = "unknown";
                info.shell = "sh";
            }

            return info;
        }

        public static ClientRegisterParams BuildRegisterParams()
        {
            return new ClientRegisterParams
            {
                client_id = ClientId,
                instance_id = InstanceIdValue,
                name = "neon-companion",
                protocol_version = 1,
                platform = DetectPlatform(),
                capabilities = new ClientCapabilitiesPayload
                {
                    terminal = new TerminalCapability
                    {
                        version = 1,
                        streaming = false,
                        cancel = false
                    },
                    file_transfer = new FileTransferCapability
                    {
                        version = 1,
                        chunk_size_max = FileTransferProtocol.DefaultChunkSizeMax,
                        resume = false
                    }
                }
            };
        }
    }
}