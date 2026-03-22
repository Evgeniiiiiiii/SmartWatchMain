namespace SmartWatchProj.Models
{
    public class Employee
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? FaceData { get; set; }
        public string? CardId { get; set; } // Для ID карты
    }
}