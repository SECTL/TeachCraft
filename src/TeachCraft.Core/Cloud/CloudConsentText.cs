namespace TeachCraft.Core.Cloud;

/// <summary>
/// 名单上传的同意文案。文案单点维护，界面与测试都从这里取，
/// 避免 XAML 里另写一份导致两边说法不一致。
/// </summary>
public static class CloudConsentText
{
    /// <summary>必须上传的字段。</summary>
    public const string RequiredFields = "姓名、性别、学号";

    /// <summary>可选字段，默认全部关闭，由用户逐项勾选。</summary>
    public const string OptionalFields = "身高、视力、成绩、备注、原小组";

    public static readonly string[] RequiredFieldNames = { "姓名", "性别", "学号" };
    public static readonly string[] OptionalFieldNames = { "身高", "视力", "成绩", "备注", "原小组" };

    public const string UncheckedFieldsNotWritten = "未勾选的字段不会写入云端";
    public const string NoRawFiles = "不会发送原始 Excel / CSV 文件或文件路径";
    public const string NoAutoUpload = "也不会自动上传";
    public const string UserDecides = "是否上传完全由你决定";
    public const string CheckSchoolPolicy = "请自行确认名单上传符合你所在学校的数据管理规定";
}
