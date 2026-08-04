package parser

import (
	"testing"

	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
)

func TestCanonicalWeapon(t *testing.T) {
	tests := map[string]string{
		"AK-47":                    "ak47",
		"M4A4":                     "m4a4",
		"M4A1":                     "m4a1",
		"M4A1-S":                   "m4a1_silencer",
		"Galil AR":                 "galilar",
		"Dual Berettas":            "elite",
		"MP5-SD":                   "mp5sd",
		"Sawed-Off":                "sawedoff",
		"Desert Eagle":             "deagle",
		"weapon_m4a1_silencer_off": "m4a1_silencer",
		"CZ75 Auto":                "cz75a",
		"weapon_knife_karambit":    "knife",
		"usp_silencer_off":         "usp_silencer",
		"Knife":                    "knife",
		"Knife Karambit":           "knife",
		"future weapon":            "unknown",
	}
	for input, expected := range tests {
		if actual := canonicalWeapon(input); actual != expected {
			t.Errorf("canonicalWeapon(%q) = %q, want %q", input, actual, expected)
		}
	}
}

func TestCanonicalEquipmentPreservesM4Variants(t *testing.T) {
	if got := canonicalEquipment(&common.Equipment{Type: common.EqM4A4}); got != "m4a4" {
		t.Fatalf("M4A4 equipment mapped to %q", got)
	}
	if got := canonicalEquipment(&common.Equipment{Type: common.EqM4A1}); got != "m4a1_silencer" {
		t.Fatalf("M4A1 equipment mapped to %q", got)
	}
}
