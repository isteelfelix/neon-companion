// FileTransferProtocol.cs - File transfer WS protocol DTOs and constants

using System;

namespace NeonCompanion.Runtime.Api.Hermes
{
    public static class FileTransferProtocol
    {
        public const int DefaultChunkSizeMax = 262144;
        public const string EncodingBase64 = "base64";
        public const string PartFileSuffix = ".part";

        public const string RootDownloads = "downloads";
        public const string RootWorkspace = "workspace";
        public const string RootTemp = "temp";
        public const string RootSession = "session";

        public const string DirectionToClient = "to_client";
        public const string DirectionFromClient = "from_client";
    }

    [Serializable]
    public class FileTransferLocation
    {
        public string root;
        public string path;
    }

    [Serializable]
    public class FileTransferStartPayload
    {
        public string transfer_id;
        public string direction;
        public FileTransferLocation source;
        public FileTransferLocation destination;
        public long size;
        public string sha256;
        public int chunk_size;
    }

    [Serializable]
    public class FileTransferChunkPayload
    {
        public string transfer_id;
        public int seq;
        public long offset;
        public string encoding;
        public string data;
    }

    [Serializable]
    public class FileTransferAckParams
    {
        public string transfer_id;
        public long next_offset;
        public int accepted_chunk_size;
    }

    [Serializable]
    public class FileTransferFinishPayload
    {
        public string transfer_id;
        public long size;
        public string sha256;
    }

    [Serializable]
    public class FileTransferCompleteParams
    {
        public string transfer_id;
        public bool verified;
        public string error;
        public long size;
        public string sha256;
        public string path;
    }
}