namespace VLMulti.Core.Settings;

// Hướng chạy khi kéo quái. App không biết quái ở đâu (bản đồ là canvas), nên hướng do bạn chọn.
public static class KiteDirections
{
    // Theo minimap: tâm phòng → phải → chéo xuống → xuống → về tâm (xem agent.js). Offset bên dưới
    // (phải → xuống → trái → lên) chỉ dùng khi không đọc được minimap.
    public const string Square = "square";
    public const string Alternate = "alternate"; // lần lượt trái rồi phải
    public const string Left = "left";
    public const string Right = "right";
    public const string Up = "up";
    public const string Down = "down";

    public static readonly string[] All = { Square, Alternate, Left, Right, Up, Down };

    public static string Label(string direction) => direction switch
    {
        Square => "Hình vuông quanh tâm phòng (đọc minimap)",
        Left => "Chạy sang trái",
        Right => "Chạy sang phải",
        Up => "Chạy lên trên",
        Down => "Chạy xuống dưới",
        _ => "Luân phiên trái / phải",
    };

    // Độ lệch so với vị trí nhân vật (tâm màn hình) cho lần kéo thứ `step` (0, 1, 2…)
    public static (int Dx, int Dy) Offset(string direction, int distance, int step) => direction switch
    {
        Square => (step % 4) switch
        {
            0 => (distance, 0),
            1 => (0, distance),
            2 => (-distance, 0),
            _ => (0, -distance),
        },
        Left => (-distance, 0),
        Right => (distance, 0),
        Up => (0, -distance),
        Down => (0, distance),
        _ => (step % 2 == 0 ? -distance : distance, 0),
    };
}
