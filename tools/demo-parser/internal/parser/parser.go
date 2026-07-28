package parser

import (
	"fmt"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"unicode/utf8"

	demoinfocs "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/events"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/msg"

	"github.com/nikitoo0os/cs-editprof/tools/demo-parser/internal/contract"
)

const (
	SchemaVersion = "1.1"
	ParserName    = "cs2-demo-parser"
	ParserVersion = "0.2.0"
)

type roundBuilder struct {
	number        int
	startTick     int64
	freezeEndTick *int64
}

func Analyze(path string) (contract.Analysis, error) {
	file, err := os.Open(path)
	if err != nil {
		return contract.Analysis{}, fmt.Errorf("open demo: %w", err)
	}
	defer file.Close()

	p := demoinfocs.NewParser(file)
	defer p.Close()

	result := contract.Analysis{
		SchemaVersion: SchemaVersion,
		Parser:        contract.ParserInfo{Name: ParserName, Version: ParserVersion},
		Demo:          contract.DemoMetadata{FileName: filepath.Base(path)},
		Players:       []contract.Player{},
		Rounds:        []contract.Round{},
		Kills:         []contract.Kill{},
		Warnings:      []string{},
	}
	players := make(map[string]contract.Player)
	matchStarted := false
	var currentRound *roundBuilder
	roundNumber := 0
	shotsSinceLastKill := make(map[string]int)
	weaponFireEvents := 0

	currentTick := func() int64 {
		return int64(p.GameState().IngameTick())
	}
	collectPlayer := func(player *common.Player) {
		if player == nil {
			return
		}
		mapped := mapPlayer(player)
		players[mapped.PlayerID] = mapped
	}
	collectParticipants := func() {
		for _, player := range p.GameState().Participants().All() {
			collectPlayer(player)
		}
	}
	startMatch := func() {
		if matchStarted {
			return
		}
		matchStarted = true
		roundNumber = 0
		currentRound = nil
		result.Rounds = result.Rounds[:0]
		result.Kills = result.Kills[:0]
		collectParticipants()
	}

	p.RegisterNetMessageHandler(func(serverInfo *msg.CSVCMsg_ServerInfo) {
		if mapName := serverInfo.GetMapName(); mapName != "" {
			result.Demo.MapName = mapName
		}
	})
	p.RegisterEventHandler(func(events.MatchStart) { startMatch() })
	p.RegisterEventHandler(func(event events.MatchStartedChanged) {
		if event.NewIsStarted {
			startMatch()
		}
	})
	p.RegisterEventHandler(func(events.AnnouncementMatchStarted) { startMatch() })
	p.RegisterEventHandler(func(events.RoundStart) {
		if !matchStarted && p.GameState().IsMatchStarted() {
			startMatch()
		}
		if !matchStarted {
			return
		}
		roundNumber++
		clear(shotsSinceLastKill)
		currentRound = &roundBuilder{number: roundNumber, startTick: currentTick()}
		collectParticipants()
	})
	p.RegisterEventHandler(func(events.RoundFreezetimeEnd) {
		if currentRound == nil {
			return
		}
		tick := currentTick()
		currentRound.freezeEndTick = &tick
	})
	p.RegisterEventHandler(func(event events.RoundEnd) {
		if currentRound == nil {
			return
		}
		result.Rounds = append(result.Rounds, contract.Round{
			RoundNumber:   currentRound.number,
			StartTick:     currentRound.startTick,
			FreezeEndTick: currentRound.freezeEndTick,
			EndTick:       currentTick(),
			Winner:        teamName(event.Winner),
			Reason:        roundEndReason(event.Reason),
		})
		currentRound = nil
		collectParticipants()
	})
	p.RegisterEventHandler(func(event events.WeaponFire) {
		if !matchStarted || currentRound == nil || event.Shooter == nil {
			return
		}
		player := mapPlayer(event.Shooter)
		shotsSinceLastKill[player.PlayerID]++
		weaponFireEvents++
	})
	p.RegisterEventHandler(func(event events.Kill) {
		if !matchStarted || currentRound == nil || event.Victim == nil {
			return
		}
		collectPlayer(event.Killer)
		collectPlayer(event.Victim)
		collectPlayer(event.Assister)
		killerID, killerName, killerTeam := nullablePlayer(event.Killer)
		assisterID, _, _ := nullablePlayer(event.Assister)
		victim := mapPlayer(event.Victim)
		weapon := "unknown"
		if event.Weapon != nil {
			weapon = canonicalWeapon(event.Weapon.String())
		}
		wallbang := event.IsWallBang()
		noScope := event.NoScope
		throughSmoke := event.ThroughSmoke
		var killerHealth *int
		var distanceMeters *float64
		var shots *int
		var oneTap *bool
		if event.Killer != nil {
			health := event.Killer.Health()
			if health > 0 {
				killerHealth = &health
			}
			mapped := mapPlayer(event.Killer)
			if count := shotsSinceLastKill[mapped.PlayerID]; count > 0 {
				value := count
				shots = &value
				isOneTap := count == 1
				oneTap = &isOneTap
			}
			shotsSinceLastKill[mapped.PlayerID] = 0
		}
		if event.Distance > 0 {
			value := float64(event.Distance)
			distanceMeters = &value
		}
		result.Kills = append(result.Kills, contract.Kill{
			EventIndex:         len(result.Kills) + 1,
			Tick:               currentTick(),
			RoundNumber:        currentRound.number,
			KillerPlayerID:     killerID,
			KillerName:         killerName,
			VictimPlayerID:     victim.PlayerID,
			VictimName:         victim.Name,
			AssisterPlayerID:   assisterID,
			Weapon:             weapon,
			Headshot:           event.IsHeadshot,
			KillerTeam:         killerTeam,
			VictimTeam:         teamName(event.Victim.Team),
			Wallbang:           &wallbang,
			OneTap:             oneTap,
			NoScope:            &noScope,
			ThroughSmoke:       &throughSmoke,
			KillerHealth:       killerHealth,
			DistanceMeters:     distanceMeters,
			ShotsSinceLastKill: shots,
		})
	})

	if err := p.ParseToEnd(); err != nil {
		return contract.Analysis{}, fmt.Errorf("parse demo: %w", err)
	}
	collectParticipants()

	result.Demo.TickRate = int(math.Round(p.TickRate()))
	result.Demo.DurationTicks = int64(p.GameState().IngameTick())
	if result.Demo.DurationTicks <= 0 {
		result.Demo.DurationTicks = int64(p.CurrentFrame())
		result.Warnings = append(result.Warnings, "Server in-game tick was unavailable; duration uses demo frames.")
	}
	if elapsed := p.CurrentTime(); elapsed > 0 {
		ms := elapsed.Milliseconds()
		result.Demo.DurationMilliseconds = &ms
	}
	if result.Demo.MapName == "" {
		result.Warnings = append(result.Warnings, "Map name was not emitted by the demo.")
	}
	if currentRound != nil {
		result.Warnings = append(result.Warnings, "The final round did not emit RoundEnd and was omitted.")
	}

	for _, player := range players {
		result.Players = append(result.Players, player)
	}
	sort.Slice(result.Players, func(i, j int) bool {
		return result.Players[i].PlayerID < result.Players[j].PlayerID
	})
	sort.SliceStable(result.Kills, func(i, j int) bool {
		if result.Kills[i].Tick == result.Kills[j].Tick {
			return result.Kills[i].EventIndex < result.Kills[j].EventIndex
		}
		return result.Kills[i].Tick < result.Kills[j].Tick
	})
	for index := range result.Kills {
		result.Kills[index].EventIndex = index + 1
	}
	markRoundEndingKills(&result)
	if weaponFireEvents == 0 {
		result.Warnings = append(
			result.Warnings,
			"WeaponFire events were unavailable; oneTap and shotsSinceLastKill are null.")
	}
	result.Warnings = append(
		result.Warnings,
		"lastEnemyKill is unavailable in parser v0.2.0 and remains null.")

	if result.Demo.TickRate <= 0 {
		return contract.Analysis{}, fmt.Errorf("required tick rate could not be extracted")
	}
	if len(result.Rounds) == 0 {
		return contract.Analysis{}, fmt.Errorf("required round events could not be extracted")
	}
	return result, nil
}

func markRoundEndingKills(result *contract.Analysis) {
	roundEnds := make(map[int]int64, len(result.Rounds))
	for _, round := range result.Rounds {
		roundEnds[round.RoundNumber] = round.EndTick
	}
	lastByRound := make(map[int]int)
	for index := range result.Kills {
		lastByRound[result.Kills[index].RoundNumber] = index
	}
	tolerance := int64(result.Demo.TickRate)
	for round, index := range lastByRound {
		value := false
		if endTick, ok := roundEnds[round]; ok {
			delta := endTick - result.Kills[index].Tick
			value = delta >= 0 && delta <= tolerance
		}
		result.Kills[index].RoundEndingKill = &value
	}
}

func canonicalWeapon(value string) string {
	aliases := map[string]string{
		"AK-47":        "ak47",
		"M4A4":         "m4a1",
		"M4A1-S":       "m4a1_silencer",
		"AWP":          "awp",
		"SSG 08":       "ssg08",
		"Desert Eagle": "deagle",
		"Glock-18":     "glock",
		"USP-S":        "usp_silencer",
		"Zeus x27":     "taser",
	}
	if code, ok := aliases[value]; ok {
		return code
	}
	if len(value) >= 5 && value[:5] == "Knife" {
		return "knife"
	}
	return "unknown"
}

func mapPlayer(player *common.Player) contract.Player {
	name := truncateUTF8(player.Name, 128)
	if player.SteamID64 == 0 {
		return contract.Player{
			PlayerID: "user:" + strconv.Itoa(player.UserID),
			SteamID:  nil,
			Name:     name,
		}
	}
	steamID := strconv.FormatUint(player.SteamID64, 10)
	return contract.Player{PlayerID: steamID, SteamID: &steamID, Name: name}
}

func nullablePlayer(player *common.Player) (*string, *string, *string) {
	if player == nil {
		return nil, nil, nil
	}
	mapped := mapPlayer(player)
	id := mapped.PlayerID
	name := mapped.Name
	return &id, &name, teamName(player.Team)
}

func teamName(team common.Team) *string {
	var name string
	switch team {
	case common.TeamTerrorists:
		name = "T"
	case common.TeamCounterTerrorists:
		name = "CT"
	default:
		return nil
	}
	return &name
}

func roundEndReason(reason events.RoundEndReason) *string {
	names := map[events.RoundEndReason]string{
		events.RoundEndReasonTargetBombed:        "TargetBombed",
		events.RoundEndReasonBombDefused:         "BombDefused",
		events.RoundEndReasonCTWin:               "CTWin",
		events.RoundEndReasonTerroristsWin:       "TerroristsWin",
		events.RoundEndReasonDraw:                "Draw",
		events.RoundEndReasonTargetSaved:         "TargetSaved",
		events.RoundEndReasonTerroristsSurrender: "TerroristsSurrender",
		events.RoundEndReasonCTSurrender:         "CTSurrender",
	}
	name, ok := names[reason]
	if !ok {
		return nil
	}
	return &name
}

func truncateUTF8(value string, maxRunes int) string {
	if utf8.RuneCountInString(value) <= maxRunes {
		return value
	}
	return string([]rune(value)[:maxRunes])
}
