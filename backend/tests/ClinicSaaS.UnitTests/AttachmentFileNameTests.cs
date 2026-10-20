using ClinicSaaS.Application.Attachments;
using FluentAssertions;

namespace ClinicSaaS.UnitTests;

/// <summary>P1-02: the client's file name is display data only; sanitizing keeps it harmless and bounded.</summary>
public sealed class AttachmentFileNameTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("....", "")]
    [InlineData("../..", "")]
    [InlineData("dir/sub\\file.pdf", "file.pdf")]
    [InlineData("tab\there\r\nnew.pdf", "tabherenew.pdf")]
    [InlineData("zero​width⁦iso⁩.png", "zerowidthiso.png")]
    [InlineData("تحليل الدم.pdf", "تحليل الدم.pdf")]
    [InlineData("report.pdf.", "report.pdf")]
    public void Sanitizes_to_a_plain_display_name(string? input, string expected) =>
        AttachmentService.SanitizeFileName(input).Should().Be(expected);

    [Fact]
    public void Long_names_are_cut_to_255_characters_keeping_the_extension()
    {
        var name = AttachmentService.SanitizeFileName(new string('a', 400) + ".jpeg");
        name.Should().HaveLength(AttachmentService.MaxFileNameLength).And.EndWith(".jpeg");
    }
}
