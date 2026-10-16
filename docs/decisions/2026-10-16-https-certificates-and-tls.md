# https: certificates checked, and TLS on Linux and macOS (2026-10-16)

FFmpeg 8.1's TLS doesn't check the server's certificate unless `tls_verify` is set (TLS_VERIFY_DEFAULT is 0 until
libavformat 63), and nothing of ours set it: https, in-process on Windows (Schannel) and by the executable, accepted a
self-signed server (shown locally, both ways). Asked: **always verify, in its own PR first**, over verifying with an
opt-out option and CLI flag (more API to keep stable) or keeping FFmpeg's default. FrameReader sets `tls_verify=1`
unless the caller's InputOptions say otherwise; ShotDetector passes `-tls_verify 1` to the executable for http(s).

Then https in our build beyond Windows, asked with the choices from FFmpeg 8.1.3's configure: **a static OpenSSL 3 on
Linux and Secure Transport on macOS** (OpenSSL 3 needs no --enable-version3 in an LGPL build; estimated at about 4 MB
more, measured at about 7 MB in libavformat once libavutil's copy was patched out; we ship
its security fixes, upstream.yml to watch it; Secure Transport is the system's, TLS 1.2 at most), over the system's
OpenSSL (libavformat wouldn't load without libssl.so.3), a static GnuTLS (nettle, GMP, libtasn1 to keep current) or
leaving https to the executable there (mbedTLS would have made the build LGPL-3). CA certificates on Linux:
**SSL_CERT_FILE, else the first bundle of the usual paths** (built with --openssldir=/etc/ssl for Debian, Ubuntu,
Alpine; /etc/pki on RHEL and Fedora), over /etc/ssl only.
