﻿using System;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http;
using System.Net.Security;

namespace RaftNode
{
    internal sealed class RaftClientHandlerFactory : IHttpMessageHandlerFactory
    {
        private static bool AllowCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true;

        public HttpMessageHandler CreateHandler(string name)
        {
            var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(100) };
            handler.SslOptions.RemoteCertificateValidationCallback = AllowCertificate;
            return handler;
        }
    }
}
