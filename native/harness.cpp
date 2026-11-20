// harness.cpp —— 纯控制台探针：不牵扯 WinUI/D3D11，只验证这套 FFmpeg 能不能开文件、解码。
#include <cstdio>
#include <cstring>

extern "C" {
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>
#include <libavutil/error.h>
}

static void printErr(const char* what, int ret)
{
    char buf[AV_ERROR_MAX_STRING_SIZE] = {};
    av_strerror(ret, buf, sizeof(buf));
    printf("  %s failed: %d (%s)\n", what, ret, buf);
}

int main(int argc, char** argv)
{
    printf("avutil=%u avcodec=%u avformat=%u\n",
           avutil_version(), avcodec_version(), avformat_version());

    av_log_set_level(AV_LOG_INFO);

    const char* path = (argc > 1) ? argv[1] : "C:\\Temp\\mltest.mp4";
    printf("opening: %s\n", path);

    AVFormatContext* fmt = nullptr;
    int ret = avformat_open_input(&fmt, path, nullptr, nullptr);
    if (ret < 0)
    {
        printErr("avformat_open_input", ret);
        return 1;
    }

    ret = avformat_find_stream_info(fmt, nullptr);
    if (ret < 0)
    {
        printErr("avformat_find_stream_info", ret);
        return 1;
    }

    const AVCodec* codec = nullptr;
    int idx = av_find_best_stream(fmt, AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
    if (idx < 0 || !codec)
    {
        printf("no video stream (%d)\n", idx);
        return 1;
    }
    printf("video stream #%d codec=%s %dx%d\n", idx, codec->name,
           fmt->streams[idx]->codecpar->width, fmt->streams[idx]->codecpar->height);

    AVCodecContext* dec = avcodec_alloc_context3(codec);
    avcodec_parameters_to_context(dec, fmt->streams[idx]->codecpar);
    if (avcodec_open2(dec, codec, nullptr) < 0)
    {
        printf("avcodec_open2 failed\n");
        return 1;
    }

    AVPacket* pkt = av_packet_alloc();
    AVFrame* frame = av_frame_alloc();
    int decoded = 0;

    while (decoded < 3 && av_read_frame(fmt, pkt) >= 0)
    {
        if (pkt->stream_index == idx)
        {
            if (avcodec_send_packet(dec, pkt) == 0)
            {
                while (avcodec_receive_frame(dec, frame) == 0)
                {
                    printf("  frame %d: %dx%d fmt=%d\n", decoded++,
                           frame->width, frame->height, frame->format);
                    av_frame_unref(frame);
                    if (decoded >= 3) break;
                }
            }
        }
        av_packet_unref(pkt);
    }

    printf("decoded %d frames\n", decoded);

    av_frame_free(&frame);
    av_packet_free(&pkt);
    avcodec_free_context(&dec);
    avformat_close_input(&fmt);
    return decoded > 0 ? 0 : 2;
}
