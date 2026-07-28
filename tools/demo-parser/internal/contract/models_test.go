package contract

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestSteamIDSerializesAsString(t *testing.T) {
	steamID := "76561198000000001"
	value := Player{PlayerID: steamID, SteamID: &steamID, Name: "Player"}

	data, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(data), `"steamId":"76561198000000001"`) {
		t.Fatalf("SteamID was not serialized as a string: %s", data)
	}
}

func TestNilSteamIDSerializesExplicitly(t *testing.T) {
	data, err := json.Marshal(Player{PlayerID: "user:7", SteamID: nil, Name: "Bot"})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(data), `"steamId":null`) {
		t.Fatalf("nil SteamID was not explicit: %s", data)
	}
}

func TestUnavailableKillSignalsAreSerializedAsNull(t *testing.T) {
	value, err := json.Marshal(Kill{
		EventIndex:     1,
		Tick:           100,
		RoundNumber:    1,
		VictimPlayerID: "victim",
		VictimName:     "Victim",
		Weapon:         "unknown",
	})
	if err != nil {
		t.Fatal(err)
	}
	text := string(value)
	for _, field := range []string{
		`"oneTap":null`,
		`"lastEnemyKill":null`,
		`"killerHealth":null`,
		`"distanceMeters":null`,
	} {
		if !strings.Contains(text, field) {
			t.Fatalf("expected %s in %s", field, text)
		}
	}
}
