import unittest

from music_analyzer import (
    classify_section,
    composite_drop_score,
    normalize,
)


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

    def test_calm_requires_more_than_low_volume(self):
        result = classify_section(
            energy=0.2,
            energy_slope=0.0,
            bass=0.1,
            onset_density=0.1,
            spectral_flux=0.1,
            novelty=0.1,
            downbeat_near=0.0,
        )
        self.assertEqual("Calm", result)

    def test_build_up_uses_slope_density_and_flux(self):
        result = classify_section(
            energy=0.5,
            energy_slope=0.35,
            bass=0.2,
            onset_density=0.8,
            spectral_flux=0.8,
            novelty=0.6,
            downbeat_near=0.0,
        )
        self.assertEqual("BuildUp", result)

    def test_drop_requires_bass_onset_slope_and_structure(self):
        result = classify_section(
            energy=0.85,
            energy_slope=0.4,
            bass=0.9,
            onset_density=0.9,
            spectral_flux=0.8,
            novelty=0.9,
            downbeat_near=1.0,
        )
        self.assertEqual("Drop", result)

    def test_false_loud_peak_is_not_drop(self):
        result = classify_section(
            energy=1.0,
            energy_slope=0.0,
            bass=0.1,
            onset_density=0.1,
            spectral_flux=0.1,
            novelty=0.1,
            downbeat_near=0.0,
        )
        self.assertNotEqual("Drop", result)


if __name__ == "__main__":
    unittest.main()
