using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Content;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>P-1 (D-1320..): the web's rules html drawn as UI Toolkit rich text.</summary>
    [TestFixture]
    public class CardFaceMarkupTests
    {
        private static CardTextData Text => UiData.Load(TestContent.Source).CardText;

        private const string Up = "#205e2b";
        private const string Down = "#8d2424";

        private const string Lines =
            "<span class=\"face-line\" data-face-kind=\"damage\">Inflict <span class=\"val up\">9</span> damage</span>" +
            "<span class=\"face-line\" data-face-kind=\"status\">Poison <span class=\"val down\">2</span></span>" +
            "<span class=\"face-line\" data-face-kind=\"detail\" data-face-extra=\"true\">Poison: <span class=\"val\">50</span>% chance</span>";

        [Test]
        public void ANumberThePreviewMovedIsTintedAndBold()
        {
            var rich = CardFaceMarkup.ToRich(Text, Lines, new CardMarkupStyle { Up = Up, Down = Down });
            Assert.That(rich, Is.EqualTo("Inflict <color=#205e2b><b>9</b></color> damage\nPoison <color=#8d2424><b>2</b></color>"));
        }

        [Test]
        public void TheLinesHeldBackAtRestAppearWhenTheCardIsSelected()
        {
            var rich = CardFaceMarkup.ToRich(Text, Lines, new CardMarkupStyle { Up = Up, Down = Down, ShowExtra = true });
            Assert.That(rich, Is.EqualTo("Inflict <color=#205e2b><b>9</b></color> damage\nPoison <color=#8d2424><b>2</b></color>\nPoison: 50% chance"));
        }

        [Test]
        public void ADamageNumberWithACalculationIsUnderlinedAndLinked()
        {
            var rich = CardFaceMarkup.ToRich(Text, Lines, new CardMarkupStyle { Up = Up, Down = Down, Receipt = true });
            Assert.That(rich, Does.StartWith("Inflict <link=\"damage\"><u><color=#205e2b><b>9</b></color></u></link> damage"));
            Assert.That(rich, Does.Contain("Poison <color=#8d2424><b>2</b></color>"), "only a damage line carries the link");
        }

        [Test]
        public void EntitiesAreText()
        {
            var rich = CardFaceMarkup.ToRich(Text, "Reveal the selected enemy&#39;s intent &amp; <span class=\"kw\">Exhaust</span>.", new CardMarkupStyle());
            Assert.That(rich, Is.EqualTo("Reveal the selected enemy's intent & Exhaust."));
        }

        [Test]
        public void PlainTextHasNoMarkup()
        {
            Assert.That(CardFaceMarkup.Plain(Text, Lines), Is.EqualTo("Inflict 9 damage\nPoison 2\nPoison: 50% chance"));
        }
    }
}
