﻿using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Synnduit.Deduplication
{
    [TestClass]
    public class IgnoreDateTimeTimeHomogenizerTest
    {
        [TestMethod]
        public void Trims_Time_Portion_Of_DateTime_Values()
        {
            HomogenizerTester.HomogenizesValuesOfSupportedType(
                new IgnoreDateTimeTimeHomogenizer(),
                new[] {
                    new DateTime(2002, 3, 28, 10, 24, 22),
                    new DateTime(2002, 3, 28)
                },
                new[] { DateTime.Now, DateTime.Today },
                new[] {
                    new DateTime(2016, 12, 27, 12, 18, 5),
                    new DateTime(2016, 12, 27)
                });
        }

        [TestMethod]
        public void Does_Not_Affect_Non_DateTime_Values()
        {
            HomogenizerTester.DoesNotAffectValuesOfUnsupportedTypes<
                IgnoreDateTimeTimeHomogenizer, DateTime>();
        }
    }
}
