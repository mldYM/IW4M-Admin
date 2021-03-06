﻿namespace SharedLibraryCore.Dtos
{
    public class FindClientRequest : PaginationInfo
    {
        /// <summary>
        /// name of client
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// network id of client
        /// </summary>
        public string Xuid { get; set; }

        public string ToDebugString() => $"[Name={Name}, Xuid={Xuid}]";
    }
}
