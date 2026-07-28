import unittest

from music_analyzer import composite_drop_score, normalize


class AnalyzerMathTests(unittest.TestCase):
    def test_normalize_is_bounded_and_deterministic(self):
        self.assertEqual([0.0, 0.5, 1.0], normalize([0, 2, 4]))
        self.assertEqual([0.0, 0.5, 1.0], normalize([0, 2, 4]))

    def test_structural_drop_scores_above_loud_unstructured_onset(self):
        structural = composite_drop_score(0.9, 0.9, 0.8, 1.0, 1.0, 0.9)
        loud_only = composite_drop_score(1.0, 0.0, 0.0, 0.0, 0.0, 0.9)
        self.assertGreater(structural, loud_only)
        self.assertGreaterEqual(structural, 0.65)

    def test_low_confidence_is_penalized(self):
        confident = composite_drop_score(1, 1, 1, 1, 1, 1)
        uncertain = composite_drop_score(1, 1, 1, 1, 1, 0)
        self.assertGreater(confident, uncertain)


if __name__ == "__main__":
    unittest.main()
