#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.FaceAnalysis.h>
#include <winrt/Windows.Storage.Streams.h>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <fcntl.h>
#include <io.h>
#include <vector>

using namespace winrt::Windows::Graphics::Imaging;
using namespace winrt::Windows::Media::FaceAnalysis;
using namespace winrt::Windows::Storage::Streams;

// Exposure and subject motion can change a few blocks substantially. A cut
// requires both a large mean difference and a majority of changed blocks.
using Thumbnail = std::array<int, 24 * 16>;

Thumbnail summarize(std::vector<uint8_t> const& pixels, int width, int height)
{
    Thumbnail tile{}, count{};
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x)
        {
            auto i = (y * 16 / height) * 24 + x * 24 / width;
            tile[i] += pixels[y * width + x];
            ++count[i];
        }
    for (size_t i = 0; i < tile.size(); ++i)
        if (count[i]) tile[i] /= count[i];
    return tile;
}

bool scene_changed(Thumbnail const& previous, Thumbnail const& current)
{
    int sum = 0, changed = 0;
    for (size_t i = 0; i < current.size(); ++i)
    {
        int difference = std::abs(previous[i] - current[i]);
        sum += difference;
        if (difference > 28) ++changed;
    }
    return sum > 35 * static_cast<int>(current.size()) && changed * 2 > static_cast<int>(current.size());
}

int main()
{
    try
    {
        _setmode(_fileno(stdin), _O_BINARY);
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        if (!FaceDetector::IsSupported() || !FaceDetector::IsBitmapPixelFormatSupported(BitmapPixelFormat::Gray8))
            return 2;
        auto detector = FaceDetector::CreateAsync().get();
        if (!detector) return 2;
        std::puts("READY");
        std::fflush(stdout);
        Thumbnail previous{};
        bool first = true;
        char line[64];
        while (std::fgets(line, sizeof(line), stdin))
        {
            int width = 0, height = 0;
            if (std::strcmp(line, "P5\n") || !std::fgets(line, sizeof(line), stdin) ||
                sscanf_s(line, "%d %d", &width, &height) != 2 ||
                width < 1 || width > 640 || height < 1 || height > 640 ||
                !std::fgets(line, sizeof(line), stdin) || std::strcmp(line, "255\n"))
                return 4;
            std::vector<uint8_t> pixels(width * height);
            if (std::fread(pixels.data(), 1, pixels.size(), stdin) != pixels.size()) return 4;
            auto current = summarize(pixels, width, height);
            DataWriter writer;
            writer.WriteBytes(pixels);
            auto image = SoftwareBitmap::CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat::Gray8, width, height);
            auto faces = detector.DetectFacesAsync(image).get();
            std::printf("%d %d %d", width, height, !first && scene_changed(previous, current));
            for (auto const& face : faces)
            {
                auto box = face.FaceBox();
                std::printf(" %u %u %u %u", box.X, box.Y, box.Width, box.Height);
            }
            std::putchar('\n');
            std::fflush(stdout);
            previous = current;
            first = false;
        }
        return std::ferror(stdin) ? 4 : 0;
    }
    catch (winrt::hresult_error const&) { return 3; }
    catch (std::exception const&) { return 3; }
}
