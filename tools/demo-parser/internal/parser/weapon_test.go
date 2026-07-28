package parser

import "testing"

func TestCanonicalWeapon(t *testing.T) {
	tests := map[string]string{
		"AK-47":          "ak47",
		"M4A1-S":         "m4a1_silencer",
		"Desert Eagle":   "deagle",
		"Knife":          "knife",
		"Knife Karambit": "knife",
		"future weapon":  "unknown",
	}
	for input, expected := range tests {
		if actual := canonicalWeapon(input); actual != expected {
			t.Errorf("canonicalWeapon(%q) = %q, want %q", input, actual, expected)
		}
	}
}
