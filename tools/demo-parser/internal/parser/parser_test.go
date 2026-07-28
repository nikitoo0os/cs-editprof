package parser

import (
	"testing"

	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
)

func TestMapPlayerUsesSteamID(t *testing.T) {
	player := mapPlayer(&common.Player{SteamID64: 76561198000000001, UserID: 7, Name: "Player"})
	if player.PlayerID != "76561198000000001" || player.SteamID == nil || *player.SteamID != player.PlayerID {
		t.Fatalf("unexpected player mapping: %#v", player)
	}
}

func TestMapPlayerFallsBackToUserID(t *testing.T) {
	player := mapPlayer(&common.Player{UserID: 7, Name: "Bot"})
	if player.PlayerID != "user:7" || player.SteamID != nil {
		t.Fatalf("unexpected fallback player mapping: %#v", player)
	}
}

func TestUnicodeNameIsRuneBounded(t *testing.T) {
	player := mapPlayer(&common.Player{UserID: 1, Name: "Игрок日本語", SteamID64: 1})
	if player.Name != "Игрок日本語" {
		t.Fatalf("Unicode name changed: %q", player.Name)
	}
}
