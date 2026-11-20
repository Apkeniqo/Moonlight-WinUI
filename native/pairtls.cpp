// pairtls.dll - minimal HTTPS GET with a client certificate (OpenSSL).
//
// Used by the pairing "pairchallenge" stage. The C# side cannot do this one
// itself: SChannel needs the private key in a certificate store, and the app
// may run at Low integrity (see PROGRESS.md), where writes to the user store
// are denied. OpenSSL reads the PEM files directly, same as mlsession.cpp.
//
// Exported:
//   int PT_Get(const char* host, int port, const char* target,
//              const char* certPath, const char* keyPath,
//              char* bodyOut, int bodyCap,
//              char* errOut, int errCap);
//   returns body length on success, -1 on failure (errOut gets a message).

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <openssl/err.h>
#include <openssl/ssl.h>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "crypt32.lib")

namespace
{
    std::string SslError()
    {
        unsigned long code = ERR_get_error();
        if (code == 0)
        {
            return "no OpenSSL error";
        }

        char text[256] = {};
        ERR_error_string_n(code, text, sizeof(text));
        return text;
    }

    void SetError(char* buffer, int capacity, const std::string& message)
    {
        if (buffer == nullptr || capacity <= 0)
        {
            return;
        }

        std::snprintf(buffer, (size_t)capacity, "%s", message.c_str());
    }

    SSL_CTX* g_context = nullptr;
    std::string g_contextKey;
    bool g_wsaReady = false;

    bool EnsureWinsock(std::string& error)
    {
        if (g_wsaReady)
        {
            return true;
        }

        WSADATA data = {};
        if (WSAStartup(MAKEWORD(2, 2), &data) != 0)
        {
            error = "WSAStartup failed";
            return false;
        }

        g_wsaReady = true;
        return true;
    }

    bool EnsureContext(const char* certPath, const char* keyPath, std::string& error)
    {
        std::string key = std::string(certPath) + "|" + keyPath;
        if (g_context != nullptr && g_contextKey == key)
        {
            return true;
        }

        if (g_context != nullptr)
        {
            SSL_CTX_free(g_context);
            g_context = nullptr;
        }

        g_context = SSL_CTX_new(TLS_client_method());
        if (g_context == nullptr)
        {
            error = "SSL_CTX_new: " + SslError();
            return false;
        }

        if (SSL_CTX_use_certificate_file(g_context, certPath, SSL_FILETYPE_PEM) != 1)
        {
            error = "load client certificate: " + SslError();
            return false;
        }

        if (SSL_CTX_use_PrivateKey_file(g_context, keyPath, SSL_FILETYPE_PEM) != 1)
        {
            error = "load client key: " + SslError();
            return false;
        }

        // Sunshine 是自签证书：身份靠客户端证书认定，服务端校验关掉
        SSL_CTX_set_verify(g_context, SSL_VERIFY_NONE, nullptr);
        SSL_CTX_set_min_proto_version(g_context, TLS1_2_VERSION);

        g_contextKey = key;
        return true;
    }

    bool ConnectSocket(const std::string& host, int port, int timeoutMs, SOCKET& outSocket)
    {
        outSocket = INVALID_SOCKET;

        addrinfo hints = {};
        hints.ai_family = AF_UNSPEC;
        hints.ai_socktype = SOCK_STREAM;
        hints.ai_protocol = IPPROTO_TCP;

        char portText[16] = {};
        std::snprintf(portText, sizeof(portText), "%d", port);

        addrinfo* addresses = nullptr;
        if (getaddrinfo(host.c_str(), portText, &hints, &addresses) != 0 || addresses == nullptr)
        {
            return false;
        }

        std::vector<addrinfo*> ordered;
        for (addrinfo* ai = addresses; ai != nullptr; ai = ai->ai_next)
        {
            if (ai->ai_family == AF_INET)
            {
                ordered.push_back(ai);
            }
        }
        for (addrinfo* ai = addresses; ai != nullptr; ai = ai->ai_next)
        {
            if (ai->ai_family != AF_INET)
            {
                ordered.push_back(ai);
            }
        }

        for (addrinfo* ai : ordered)
        {
            SOCKET s = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
            if (s == INVALID_SOCKET)
            {
                continue;
            }

            u_long nonBlocking = 1;
            ioctlsocket(s, FIONBIO, &nonBlocking);

            bool connected = false;
            if (connect(s, ai->ai_addr, (int)ai->ai_addrlen) == 0)
            {
                connected = true;
            }
            else if (WSAGetLastError() == WSAEWOULDBLOCK)
            {
                fd_set writable;
                FD_ZERO(&writable);
                FD_SET(s, &writable);

                timeval tv = {};
                tv.tv_sec = timeoutMs / 1000;
                tv.tv_usec = (timeoutMs % 1000) * 1000;

                if (select(0, nullptr, &writable, nullptr, &tv) > 0)
                {
                    int socketError = 0;
                    int length = (int)sizeof(socketError);
                    getsockopt(s, SOL_SOCKET, SO_ERROR, (char*)&socketError, &length);
                    connected = (socketError == 0);
                }
            }

            if (connected)
            {
                nonBlocking = 0;
                ioctlsocket(s, FIONBIO, &nonBlocking);
                outSocket = s;
                break;
            }

            closesocket(s);
        }

        freeaddrinfo(addresses);
        return outSocket != INVALID_SOCKET;
    }
}

namespace
{
    // 通用请求：method + 可选 content-type/正文。成功返回正文长度（写入 bodyOut）。
    int DoRequest(
        const char* method,
        const char* contentType,
        const char* extraHeaders,
        const void* requestBody,
        int requestBodyLength,
        const char* host,
        int port,
        const char* target,
        const char* certPath,
        const char* keyPath,
        char* bodyOut,
        int bodyCap,
        char* errOut,
        int errCap)
    {
        std::string error;

        if (host == nullptr || target == nullptr || certPath == nullptr || keyPath == nullptr)
        {
            SetError(errOut, errCap, "invalid argument");
            return -1;
        }

        if (!EnsureWinsock(error) || !EnsureContext(certPath, keyPath, error))
        {
            SetError(errOut, errCap, error);
            return -1;
        }

        SOCKET sock = INVALID_SOCKET;
        const int timeoutMs = 15000;
        if (!ConnectSocket(host, port, 10000, sock))
        {
            SetError(errOut, errCap, std::string("connect failed: ") + host + ":" + std::to_string(port));
            return -1;
        }

        DWORD receiveTimeout = (DWORD)timeoutMs;
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char*)&receiveTimeout, sizeof(receiveTimeout));

        SSL* ssl = SSL_new(g_context);
        if (ssl == nullptr)
        {
            SetError(errOut, errCap, "SSL_new: " + SslError());
            closesocket(sock);
            return -1;
        }

        SSL_set_fd(ssl, (int)sock);
        SSL_set_tlsext_host_name(ssl, host);
        SSL_set_connect_state(ssl);

        if (SSL_connect(ssl) != 1)
        {
            SetError(errOut, errCap, "TLS handshake: " + SslError());
            SSL_free(ssl);
            closesocket(sock);
            return -1;
        }

        std::string request =
            std::string(method) + " " + target + " HTTP/1.1\r\n" +
            "Host: " + host + ":" + std::to_string(port) + "\r\n" +
            "Accept: */*\r\n" +
            "User-Agent: MoonlightWinUI-pairtls/0.2\r\n";

        if (requestBody != nullptr && requestBodyLength > 0)
        {
            request += std::string("Content-Type: ") + (contentType ? contentType : "application/octet-stream") + "\r\n";
            request += "Content-Length: " + std::to_string(requestBodyLength) + "\r\n";
        }

        if (extraHeaders != nullptr && extraHeaders[0] != '\0')
        {
            request += extraHeaders;
            if (request.size() < 2 || request.compare(request.size() - 2, 2, "\r\n") != 0)
            {
                request += "\r\n";
            }
        }

        request += "Connection: close\r\n\r\n";

        if (requestBody != nullptr && requestBodyLength > 0)
        {
            request.append((const char*)requestBody, (size_t)requestBodyLength);
        }

        int sent = 0;
        const int total = (int)request.size();
        while (sent < total)
        {
            const int written = SSL_write(ssl, request.data() + sent, total - sent);
            if (written <= 0)
            {
                SetError(errOut, errCap, "send request: " + SslError());
                SSL_free(ssl);
                closesocket(sock);
                return -1;
            }
            sent += written;
        }

        std::string raw;
        std::vector<char> buffer(16384);
        for (;;)
        {
            const int read = SSL_read(ssl, buffer.data(), (int)buffer.size());
            if (read > 0)
            {
                raw.append(buffer.data(), (size_t)read);
                continue;
            }

            const int reason = SSL_get_error(ssl, read);
            if (reason == SSL_ERROR_ZERO_RETURN)
            {
                break;
            }
            if (reason == SSL_ERROR_SYSCALL && read == 0)
            {
                break;
            }
            if (!raw.empty() && (reason == SSL_ERROR_SYSCALL || reason == SSL_ERROR_WANT_READ))
            {
                break;
            }

            SetError(errOut, errCap, "read response: " + SslError());
            SSL_free(ssl);
            closesocket(sock);
            return -1;
        }

        SSL_free(ssl);
        closesocket(sock);

        const size_t headerEnd = raw.find("\r\n\r\n");
        if (headerEnd == std::string::npos)
        {
            SetError(errOut, errCap, "malformed response (no header/body split)");
            return -1;
        }

        const std::string body = raw.substr(headerEnd + 4);
        if (bodyOut == nullptr || bodyCap <= 0)
        {
            return 0;
        }

        const int copy = (int)body.size() < bodyCap - 1 ? (int)body.size() : bodyCap - 1;
        std::memcpy(bodyOut, body.data(), (size_t)copy);
        bodyOut[copy] = '\0';
        return copy;
    }
}

extern "C" __declspec(dllexport) int PT_Get(
    const char* host,
    int port,
    const char* target,
    const char* certPath,
    const char* keyPath,
    char* bodyOut,
    int bodyCap,
    char* errOut,
    int errCap)
{
    return DoRequest("GET", nullptr, nullptr, nullptr, 0, host, port, target, certPath, keyPath,
                     bodyOut, bodyCap, errOut, errCap);
}

// POST：大块数据（剪贴板大图走 /api/v1/clipboard/blob）
extern "C" __declspec(dllexport) int PT_Post(
    const char* host,
    int port,
    const char* target,
    const char* certPath,
    const char* keyPath,
    const char* contentType,
    const void* requestBody,
    int requestBodyLength,
    char* bodyOut,
    int bodyCap,
    char* errOut,
    int errCap)
{
    return DoRequest("POST", contentType, nullptr, requestBody, requestBodyLength, host, port, target,
                     certPath, keyPath, bodyOut, bodyCap, errOut, errCap);
}

// 带自定义请求头的请求（探接口 / 上传大块时用）
extern "C" __declspec(dllexport) int PT_Request(
    const char* method,
    const char* host,
    int port,
    const char* target,
    const char* certPath,
    const char* keyPath,
    const char* contentType,
    const char* extraHeaders,
    const void* requestBody,
    int requestBodyLength,
    char* bodyOut,
    int bodyCap,
    char* errOut,
    int errCap)
{
    return DoRequest(method, contentType, extraHeaders, requestBody, requestBodyLength, host, port, target,
                     certPath, keyPath, bodyOut, bodyCap, errOut, errCap);
}