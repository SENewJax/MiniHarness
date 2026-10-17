namespace SampleProject
{
    // A tiny, dependency-free domain class used as the subject under test.
    // The self-test command in Program.cs exercises ApplyDiscount against the
    // *.case files under tests/.
    public static class Calculator
    {
        // Intended semantics: subtract percent percent of price from price.
        // NOTE (sample defect): the V1 seed implementation subtracts the
        // percentage as an absolute amount, which the seeded test cases reject.
        public static int ApplyDiscount(int price, int percent)
            => price - percent;
    }
}
