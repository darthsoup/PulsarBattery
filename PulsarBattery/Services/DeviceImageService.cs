using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

/// <summary>
/// Resolves product artwork without depending on a cMouse installation. Curated URLs point at Pulsar's
/// public storefront CDN, downloads are cached locally, and packaged artwork is the offline fallback.
/// </summary>
internal static class DeviceImageService
{
    private const int MaximumImageBytes = 3 * 1024 * 1024;
    private const string CrazyLightFallback = "ms-appx:///Assets/Devices/X2-CrazyLight.png";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    static DeviceImageService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PulsarBattery/0.3 (+https://github.com/darthsoup/PulsarBattery)");
    }

    public static Uri? GetPackagedImage(string model)
    {
        var value = model.Trim();
        return value switch
        {
            "X2 CrazyLight" => new Uri(CrazyLightFallback),
            "X2 V1" => new Uri("ms-appx:///Assets/Devices/X2v1.png"),
            "X2 V3 eS" => new Uri("ms-appx:///Assets/Devices/X2v3-eS.png"),
            _ when value.StartsWith("X2 CL", StringComparison.OrdinalIgnoreCase)
                || value.Contains("X2 CrazyLight", StringComparison.OrdinalIgnoreCase) => new Uri(CrazyLightFallback),
            _ => null,
        };
    }

    public static Uri? GetOfficialImage(string model, int? protocolModelId = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var value = model.Trim();
        var profileImage = GetCmouseProfileImage(protocolModelId);
        if (profileImage is not null)
        {
            return profileImage;
        }

        // Editions with a published, unambiguous storefront image.
        if (Contains(value, "Randomfrankp")) return Cdn("Pulsar-RFP_x2crazylight_01_mini.png");
        if (Contains(value, "Boardzy")) return Cdn("Pulsar-Boardzy_x2crazylight_01-mini.png");
        if (Contains(value, "THE FINALS") || Contains(value, "The Finals")) return Cdn("Pulsar-X2-CrazyLight-Medium_THE-FINALS_front-medium_In-game-skin-black.png");
        if (Contains(value, "Bruce Lee") && Contains(value, "Medium")) return Cdn("PulsarxBruceLee_X2-CrazyLight_Medium_GamingMouse_Front_main.png");
        if (Contains(value, "Bruce Lee")) return Cdn("PulsarxBruceLee_X2-CrazyLight_Mini_GamingMouse_Front_main.png");
        if (Contains(value, "Pikachu")) return Cdn("PulsarPokemonPikachuX2CrazyLightGamingMouse_001_69e1354a-4aac-4386-98be-d9a3866b0f91.png");
        if (Contains(value, "Blue Archive")) return Cdn("Pulsar-BlueArchive-Hoshino-X2_01-medium.png");
        if (Contains(value, "T1 Edition(Red)")) return Cdn("Pulsar-T1-X2CrazyLight-Gaming-Mouse_Red_001_74d44e10-5503-4d06-9ae9-313b0b7151bc.png");
        if (Contains(value, "T1 Edition(Black)")) return Cdn("Pulsar-T1-X2CrazyLight-Gaming-Mouse_Black_001_e2f396ad-805f-4f1e-84a2-1e2182bb920d.png");
        if (Contains(value, "PRX") && !Contains(value, "Medium")) return Cdn("Pulsar-PRX-X2v3-wireless-Gaming-Mouse_size1_01_mini.png");

        // Pro-series and sensor-family models.
        if (Contains(value, "Feinmann")) return Cdn("Feinmann_x_Noctua_F01_Gaming_Mouse_top_5f96d016-1a7d-4430-896e-994b2e2173e1.png");
        if (Contains(value, "CARPE-X")) return Cdn("Pulsar-carpeX_top.png");
        if (Contains(value, "JINGGG-X")) return Cdn("Pulsar-jingggX_top.png");
        if (Contains(value, "BUZZ-X")) return Cdn("Pulsar-Buzz-X_01.png");
        if (Contains(value, "STA-X")) return Cdn("Pulsar-StaX_01.png");
        if (Contains(value, "JV-X")) return Cdn("Pulsar-JV-X_white_01_medium_def9f4e8-f2c7-4b37-8f03-92a691d6b888.png");
        if (Contains(value, "Susanto-X")) return Cdn("Pulsar-SusantoX_FRONT-medium.png");
        if (Contains(value, "ZywOo") || Contains(value, "Zywoo")) return Cdn("Pulsar-Zywoo-The-Chosen-Mouse-Gen.2_Black_Medium_01.png");
        if (Contains(value, "eS FS1")) return Cdn("Pulsar-eS-fs1-top-medium.png");
        if (Contains(value, "TenZ") || Contains(value, "Tenz")) return Cdn("TenZ.png");
        if (Contains(value, "LA-2")) return Cdn("Pulsar-X2-CrazyLight-LA-Gaming-Mouse-001_183a176e-bf2c-4bb2-8a81-e6d71855f33a.png");

        // Family artwork. Special editions no longer in the storefront fall back to their matching
        // shell rather than using files from cMouse.
        if (Contains(value, "X3 LHD"))
        {
            if (Contains(value, "Medium") && Contains(value, "Desert")) return Cdn("Pulsar_X3_CrazyLight_LHD_medium_desert_01.png");
            if (Contains(value, "Medium") && Contains(value, "White")) return Cdn("X3_LHD_crazylight_white_medium_Top_M.png");
            if (Contains(value, "Medium")) return Cdn("X3_LHD_crazylight_black_medium_Top_M.png");
            if (Contains(value, "White")) return Cdn("Pulsar-X3_lhd-white-front-mini.png");
            return Cdn("Pulsar-X3_lhd-Top-mini.png");
        }

        if (Contains(value, "X3"))
        {
            if (Contains(value, "Medium") && Contains(value, "Desert")) return Cdn("Pulsar_X3_CrazyLight_RHD_medium_desert_01.png");
            if (Contains(value, "Medium") && Contains(value, "White")) return Cdn("X3_crazylight_white_medium_Top_M.png");
            if (Contains(value, "Medium")) return Cdn("X3_crazylight_black_medium_Top_M.png");
            if (Contains(value, "Desert")) return Cdn("Pulsar_X3_CrazyLight_RHD_mini_desert_01.png");
            if (Contains(value, "White")) return Cdn("Pulsar-X3_rhd-white-Top-mini.png");
            return Cdn("Pulsar-X3_rhd-Top-mini.png");
        }

        if (Contains(value, "X2H"))
        {
            if (Contains(value, "Medium") && Contains(value, "Volcano")) return Cdn("X2H_crazylight_volcano_medium_01.png");
            if (Contains(value, "Medium") && Contains(value, "White")) return Cdn("Pulsar-X2H_medium_white_front-medium.png");
            if (Contains(value, "Medium")) return Cdn("Pulsar-X2H_medium_black_front-medium.png");
            if (Contains(value, "Volcano")) return Cdn("X2H_crazylight_volcano_mini_01.png");
            if (Contains(value, "White")) return Cdn("Pulsar-X2H_white_front-mini.png");
            return Cdn("Pulsar-X2H_black_front-mini.png");
        }

        if (Contains(value, "X2N"))
        {
            if (Contains(value, "Medium") && Contains(value, "Ocean")) return Cdn("Pulsar-X2N_OCEAN_FRONT-02.png");
            if (Contains(value, "Medium") && Contains(value, "White")) return Cdn("Pulsar-X2N_WHITE_FRONT-02.png");
            if (Contains(value, "Medium")) return Cdn("Pulsar-X2N_BLACK_FRONT-MEDIUM.png");
            if (Contains(value, "Ocean")) return Cdn("Pulsar-X2N_ocean_top-mini.png");
            if (Contains(value, "White")) return Cdn("Pulsar-X2N_white_FRONT-mini.png");
            return Cdn("Pulsar-X2N_FRONT-MINI.png");
        }

        if (Contains(value, "Xlite"))
        {
            if (Contains(value, "Large")) return Cdn("Pulsar-Xlite-v4-Gaming-Mouse_size3-Black_001_321d2e3e-d316-461b-855c-978a93a5f7cc.png");
            if (Contains(value, "Medium") && Contains(value, "Rock")) return Cdn("Xlite_crazylight_rock_medium_01.png");
            if (Contains(value, "Medium") && Contains(value, "White")) return Cdn("Pulsar-Xlite-CrazyLight_White_Medium_01-medium.png");
            if (Contains(value, "Medium")) return Cdn("Pulsar-Xlite-CrazyLight_Black_Medium_01-medium.png");
            if (Contains(value, "Rock")) return Cdn("Xlite_crazylight_rock_mini_01.png");
            if (Contains(value, "White")) return Cdn("Pulsar-Xlite-CrazyLight_White_Mini_01.png");
            return Cdn("Pulsar-Xlite-CrazyLight_Black_Mini_01-mini.png");
        }

        if (Contains(value, "X2A"))
        {
            return Contains(value, "Mini")
                ? Cdn("Pulsar_X2A_v3_Gaming_Mouse_Black_Mini_001.png")
                : Cdn("Pulsar_X2A_v3_Gaming_Mouse_Black_001.png");
        }

        if (Contains(value, "X2F") || Contains(value, "Lab. X2F"))
        {
            return Contains(value, "5th")
                ? Cdn("Pulsar-X2F_5th_01_fe19b28e-fab2-477e-8706-7babc6551a23.png")
                : Cdn("Pulsar-X2F_Black_01_mini.png");
        }

        if (Contains(value, "X2 CrazyLight") ||
            Contains(value, "X2 Crazylight") ||
            Contains(value, "X2 Mini Pulsar By You") ||
            Contains(value, "Pulsar X VSPO"))
        {
            if (Contains(value, "Medium") && Contains(value, "Forest")) return Cdn("Pulsar_X2_CrazyLight_Medium_forest_01.png");
            if (Contains(value, "Medium") && Contains(value, "White")) return Cdn("Pulsar-X2-CrazyLight_medium_white_01-medium.png");
            if (Contains(value, "Medium")) return Cdn("Pulsar-X2-CrazyLight_medium_black_01-medium.png");
            if (Contains(value, "Forest")) return Cdn("Pulsar_X2_CrazyLight_Mini_forest_01.png");
            return Cdn("Pulsar-X2-CL-jetblack_Gaming-Mouse_001_abd7a0cb-9165-4721-ba66-c1481a711c4f.png");
        }

        // X4/X5 and profiles explicitly marked "Image TBD" do not have a
        // published, trustworthy storefront image yet.
        return null;
    }

    public static async Task<Uri?> GetCachedOfficialImageAsync(
        string model,
        int? protocolModelId,
        CancellationToken cancellationToken)
    {
        var remote = GetOfficialImage(model, protocolModelId);
        if (remote is null)
        {
            return null;
        }

        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulsarBattery",
            "DeviceImages");
        var cacheName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote.AbsoluteUri))) + ".png";
        var cachePath = Path.Combine(cacheDirectory, cacheName);
        if (File.Exists(cachePath) && HasValidPng(cachePath))
        {
            return new Uri(cachePath);
        }

        Directory.CreateDirectory(cacheDirectory);
        var temporaryPath = Path.Combine(cacheDirectory, $"{cacheName}.{Guid.NewGuid():N}.part");

        try
        {
            using var response = await Http.GetAsync(remote, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.RequestMessage?.RequestUri is not Uri finalUri ||
                !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                (!string.Equals(finalUri.Host, "www.pulsar.gg", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(finalUri.Host, "cdn.shopify.com", StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaximumImageBytes)
            {
                return null;
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                var total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaximumImageBytes)
                    {
                        return null;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            if (!HasValidPng(temporaryPath))
            {
                return null;
            }

            File.Move(temporaryPath, cachePath, overwrite: true);
            return new Uri(cachePath);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return File.Exists(cachePath) && HasValidPng(cachePath) ? new Uri(cachePath) : null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // A stale .part file is harmless and can be replaced later.
            }
        }
    }

    private static Uri Cdn(string fileName) =>
        new($"https://www.pulsar.gg/cdn/shop/files/{Uri.EscapeDataString(fileName)}?width=480");

    private static Uri? GetCmouseProfileImage(int? protocolModelId)
    {
        if (protocolModelId is not int modelId || (modelId >> 8) != 0x57)
        {
            return null;
        }

        return (modelId & 0xFF) switch
        {
            1 => Cdn("Pulsar-X2-CL-jetblack_Gaming-Mouse_001_abd7a0cb-9165-4721-ba66-c1481a711c4f.png"),
            2 => Cdn("Pulsar-X2-CL-AquaZest_Gaming-Mouse_001.png"),
            3 => Cdn("Pulsar-X2-CL-X2-CL-SunsetHaze_Gaming-Mouse_001.png"),
            4 => Cdn("Pulsar-X2-CL-X2-CL-Uyuni-White_Gaming-Mouse_001.png"),
            5 => Cdn("Pulsar-X2-CL-VoltShadowGaming-Mouse_001.png"),
            6 => Cdn("Pulsar-X2-CrazyLight-LA-Gaming-Mouse-001_183a176e-bf2c-4bb2-8a81-e6d71855f33a.png"),
            37 => Cdn("Pulsar-TenZ-signature-RED-edition-wireless-Gaming-Mouse_01_size2.png"),
            40 => Cdn("TenZ.png"),
            87 => Cdn("Pulsar-BlueArchive-Hoshino-X2_01-medium.png"),
            88 => Cdn("Pulsar-BlueArchive-Shiroko-X2_01-medium.png"),
            89 => Cdn("Pulsar-BlueArchive-Nonomi-X2_01-medium.png"),
            // V1.31's fourth, unnamed Blue Archive rendering (MID 90) has no public product page, so use
            // the shell rather than mislabelling it as one of the named editions.
            90 => Cdn("Pulsar-X2-CrazyLight_medium_black_01-medium.png"),
            _ => null,
        };
    }

    private static bool Contains(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static bool HasValidPng(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[24];
            using var stream = File.OpenRead(path);
            if (stream.Read(header) != header.Length ||
                !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                return false;
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
            return width is > 0 and <= 4096 && height is > 0 and <= 4096;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
