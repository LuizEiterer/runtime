﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using static System.Net.Quic.Implementations.MsQuic.Internal.MsQuicNativeMethods;

namespace System.Net.Quic.Implementations.MsQuic.Internal
{
    internal sealed class SafeMsQuicConfigurationHandle : SafeHandle
    {
        private static readonly FieldInfo _contextCertificate = typeof(SslStreamCertificateContext).GetField("Certificate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo _contextChain = typeof(SslStreamCertificateContext).GetField("IntermediateCertificates", BindingFlags.NonPublic | BindingFlags.Instance)!;

        public override bool IsInvalid => handle == IntPtr.Zero;

        public SafeMsQuicConfigurationHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        { }

        protected override bool ReleaseHandle()
        {
            MsQuicApi.Api.ConfigurationCloseDelegate(handle);
            SetHandle(IntPtr.Zero);
            return true;
        }

        // TODO: consider moving the static code from here to keep all the handle classes small and simple.
        public static SafeMsQuicConfigurationHandle Create(QuicClientConnectionOptions options)
        {
            X509Certificate? certificate = null;

            if (options.ClientAuthenticationOptions != null)
            {
                if (options.ClientAuthenticationOptions.CipherSuitesPolicy != null)
                {
                    throw new PlatformNotSupportedException(SR.Format(SR.net_quic_ssl_option, nameof(options.ClientAuthenticationOptions.CipherSuitesPolicy)));
                }

#pragma warning disable SYSLIB0040 // NoEncryption and AllowNoEncryption are obsolete
                if (options.ClientAuthenticationOptions.EncryptionPolicy == EncryptionPolicy.NoEncryption)
                {
                    throw new PlatformNotSupportedException(SR.Format(SR.net_quic_ssl_option, nameof(options.ClientAuthenticationOptions.EncryptionPolicy)));
                }
#pragma warning restore SYSLIB0040

                if (options.ClientAuthenticationOptions.ClientCertificates != null)
                {
                    foreach (var cert in options.ClientAuthenticationOptions.ClientCertificates)
                    {
                        try
                        {
                            if (((X509Certificate2)cert).HasPrivateKey)
                            {
                                // Pick first certificate with private key.
                                certificate = cert;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            return Create(options, QUIC_CREDENTIAL_FLAGS.CLIENT, certificate: certificate, certificateContext: null, options.ClientAuthenticationOptions?.ApplicationProtocols);
        }

        public static SafeMsQuicConfigurationHandle Create(QuicOptions options, SslServerAuthenticationOptions? serverAuthenticationOptions, string? targetHost = null)
        {
            QUIC_CREDENTIAL_FLAGS flags = QUIC_CREDENTIAL_FLAGS.NONE;
            X509Certificate? certificate = serverAuthenticationOptions?.ServerCertificate;

            if (serverAuthenticationOptions != null)
            {
                if (serverAuthenticationOptions.CipherSuitesPolicy != null)
                {
                    throw new PlatformNotSupportedException(SR.Format(SR.net_quic_ssl_option, nameof(serverAuthenticationOptions.CipherSuitesPolicy)));
                }

#pragma warning disable SYSLIB0040 // NoEncryption and AllowNoEncryption are obsolete
                if (serverAuthenticationOptions.EncryptionPolicy == EncryptionPolicy.NoEncryption)
                {
                    throw new PlatformNotSupportedException(SR.Format(SR.net_quic_ssl_option, nameof(serverAuthenticationOptions.EncryptionPolicy)));
                }
#pragma warning restore SYSLIB0040

                if (serverAuthenticationOptions.ClientCertificateRequired)
                {
                    flags |= QUIC_CREDENTIAL_FLAGS.REQUIRE_CLIENT_AUTHENTICATION | QUIC_CREDENTIAL_FLAGS.INDICATE_CERTIFICATE_RECEIVED | QUIC_CREDENTIAL_FLAGS.NO_CERTIFICATE_VALIDATION;
                }

                if (certificate == null && serverAuthenticationOptions?.ServerCertificateSelectionCallback != null && targetHost != null)
                {
                    certificate = serverAuthenticationOptions.ServerCertificateSelectionCallback(options, targetHost);
                }
            }

            return Create(options, flags, certificate, serverAuthenticationOptions?.ServerCertificateContext, serverAuthenticationOptions?.ApplicationProtocols);
        }

        // TODO: this is called from MsQuicListener and when it fails it wreaks havoc in MsQuicListener finalizer.
        //       Consider moving bigger logic like this outside of constructor call chains.
        private static unsafe SafeMsQuicConfigurationHandle Create(QuicOptions options, QUIC_CREDENTIAL_FLAGS flags, X509Certificate? certificate, SslStreamCertificateContext? certificateContext, List<SslApplicationProtocol>? alpnProtocols)
        {
            // TODO: some of these checks should be done by the QuicOptions type.
            if (alpnProtocols == null || alpnProtocols.Count == 0)
            {
                throw new Exception("At least one SslApplicationProtocol value must be present in SslClientAuthenticationOptions or SslServerAuthenticationOptions.");
            }

            if (options.MaxBidirectionalStreams > ushort.MaxValue)
            {
                throw new Exception("MaxBidirectionalStreams overflow.");
            }

            if (options.MaxBidirectionalStreams > ushort.MaxValue)
            {
                throw new Exception("MaxBidirectionalStreams overflow.");
            }

            if ((flags & QUIC_CREDENTIAL_FLAGS.CLIENT) == 0)
            {
                if (certificate == null && certificateContext == null)
                {
                    throw new Exception("Server must provide certificate");
                }
            }
            else
            {
                flags |= QUIC_CREDENTIAL_FLAGS.INDICATE_CERTIFICATE_RECEIVED | QUIC_CREDENTIAL_FLAGS.NO_CERTIFICATE_VALIDATION;
            }

            if (!OperatingSystem.IsWindows())
            {
                // Use certificate handles on Windows, fall-back to ASN1 otherwise.
                flags |= QUIC_CREDENTIAL_FLAGS.USE_PORTABLE_CERTIFICATES;
            }

            Debug.Assert(!MsQuicApi.Api.Registration.IsInvalid);

            var settings = new QuicSettings
            {
                IsSetFlags = QuicSettingsIsSetFlags.PeerBidiStreamCount |
                             QuicSettingsIsSetFlags.PeerUnidiStreamCount,
                PeerBidiStreamCount = (ushort)options.MaxBidirectionalStreams,
                PeerUnidiStreamCount = (ushort)options.MaxUnidirectionalStreams
            };

            if (options.IdleTimeout != Timeout.InfiniteTimeSpan)
            {
                if (options.IdleTimeout <= TimeSpan.Zero) throw new Exception("IdleTimeout must not be negative.");

                ulong ms = (ulong)options.IdleTimeout.Ticks / TimeSpan.TicksPerMillisecond;
                if (ms > (1ul << 62) - 1) throw new Exception("IdleTimeout is too large (max 2^62-1 milliseconds)");

                settings.IdleTimeoutMs = (ulong)options.IdleTimeout.TotalMilliseconds;
            }
            else
            {
                settings.IdleTimeoutMs = 0;
            }
            settings.IsSetFlags |= QuicSettingsIsSetFlags.IdleTimeoutMs;

            uint status;
            SafeMsQuicConfigurationHandle? configurationHandle;
            X509Certificate2[]? intermediates = null;

            MemoryHandle[]? handles = null;
            QuicBuffer[]? buffers = null;
            try
            {
                MsQuicAlpnHelper.Prepare(alpnProtocols, out handles, out buffers);
                status = MsQuicApi.Api.ConfigurationOpenDelegate(MsQuicApi.Api.Registration, (QuicBuffer*)Marshal.UnsafeAddrOfPinnedArrayElement(buffers, 0), (uint)alpnProtocols.Count, ref settings, (uint)sizeof(QuicSettings), context: IntPtr.Zero, out configurationHandle);
            }
            finally
            {
                MsQuicAlpnHelper.Return(ref handles, ref buffers);
            }

            QuicExceptionHelpers.ThrowIfFailed(status, "ConfigurationOpen failed.");

            try
            {
                CredentialConfig config = default;
                config.Flags = flags; // TODO: consider using LOAD_ASYNCHRONOUS with a callback.

                if (certificateContext != null)
                {
                    certificate = (X509Certificate2?) _contextCertificate.GetValue(certificateContext);
                    intermediates = (X509Certificate2[]?) _contextChain.GetValue(certificateContext);

                    if (certificate == null || intermediates == null)
                    {
                        throw new ArgumentException(nameof(certificateContext));
                    }
                }

                if (certificate != null)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        config.Type = QUIC_CREDENTIAL_TYPE.CONTEXT;
                        config.Certificate = certificate.Handle;
                        status = MsQuicApi.Api.ConfigurationLoadCredentialDelegate(configurationHandle, ref config);
                    }
                    else
                    {
                        CredentialConfigCertificatePkcs12 pkcs12Config;
                        byte[] asn1;

                        if (intermediates?.Length > 0)
                        {
                            X509Certificate2Collection collection = new X509Certificate2Collection();
                            collection.Add(certificate);
                            for (int i= 0; i < intermediates?.Length; i++)
                            {
                                collection.Add(intermediates[i]);
                            }

                            asn1 = collection.Export(X509ContentType.Pkcs12)!;
                        }
                        else
                        {
                            asn1 = certificate.Export(X509ContentType.Pkcs12);
                        }

                        fixed (void* ptr = asn1)
                        {
                            pkcs12Config.Asn1Blob = (IntPtr)ptr;
                            pkcs12Config.Asn1BlobLength = (uint)asn1.Length;
                            pkcs12Config.PrivateKeyPassword = IntPtr.Zero;

                            config.Type = QUIC_CREDENTIAL_TYPE.PKCS12;
                            config.Certificate = (IntPtr)(&pkcs12Config);
                            status = MsQuicApi.Api.ConfigurationLoadCredentialDelegate(configurationHandle, ref config);
                        }
                    }
                }
                else
                {
                    config.Type = QUIC_CREDENTIAL_TYPE.NONE;
                    status = MsQuicApi.Api.ConfigurationLoadCredentialDelegate(configurationHandle, ref config);
                }

#if TARGET_WINDOWS
                if ((Interop.SECURITY_STATUS)status == Interop.SECURITY_STATUS.AlgorithmMismatch && MsQuicApi.Tls13MayBeDisabled)
                {
                    throw new QuicException(SR.net_ssl_app_protocols_invalid, null, (int)status);
                }
#endif

                QuicExceptionHelpers.ThrowIfFailed(status, "ConfigurationLoadCredential failed.");
            }
            catch
            {
                configurationHandle.Dispose();
                throw;
            }

            return configurationHandle;
        }
    }
}
