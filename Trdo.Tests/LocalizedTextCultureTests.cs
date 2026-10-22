using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using Trdo.Services;

namespace Trdo.Tests;

[TestClass]
public sealed class LocalizedTextCultureTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("ru-RU")]
    [DataRow("uk-UA")]
    [DataRow("de-DE")]
    public void EnglishFallback_PreservesDurationTextAcrossSystemCultures(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            Assert.AreEqual("2.5 seconds", SongChangeAnnouncementPolicy.DescribeDelay(2.5));
            Assert.AreEqual("2.5 seconds", SongChangeAnnouncementPolicy.DescribeDwell(2.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
