namespace MonitorSync.Core;

public static class BrightnessUsages
{
    // USB HID Consumer Display Brightness and Apple's published vendor usages.
    // Vendor pages are meaningful only for Apple devices (USB/Bluetooth vendor ID 05AC).
    public static int Direction(ushort page, ushort usage, uint vendorId) => (page, usage) switch
    {
        (0x000C, 0x006F) => 5,
        (0x000C, 0x0070) => -5,
        (0xFF01, 0x0020) or (0x00FF, 0x0004) when vendorId == 0x05AC => 5,
        (0xFF01, 0x0021) or (0x00FF, 0x0005) when vendorId == 0x05AC => -5,
        _ => 0
    };
}
