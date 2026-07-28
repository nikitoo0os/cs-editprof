package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestInvalidArguments(t *testing.T) {
	if code := run([]string{"analyze"}); code != 2 {
		t.Fatalf("expected exit 2, got %d", code)
	}
}

func TestMissingInput(t *testing.T) {
	output := filepath.Join(t.TempDir(), "analysis.json")
	if code := run([]string{"analyze", "--input", "missing.dem", "--output", output}); code != 10 {
		t.Fatalf("expected exit 10, got %d", code)
	}
}

func TestRejectsNonDemoExtension(t *testing.T) {
	input := filepath.Join(t.TempDir(), "input.txt")
	if err := os.WriteFile(input, []byte("not a demo"), 0o600); err != nil {
		t.Fatal(err)
	}
	output := filepath.Join(t.TempDir(), "analysis.json")
	if code := run([]string{"analyze", "--input", input, "--output", output}); code != 11 {
		t.Fatalf("expected exit 11, got %d", code)
	}
}

func TestCorruptDemoIsParserError(t *testing.T) {
	input := filepath.Join(t.TempDir(), "input.dem")
	if err := os.WriteFile(input, []byte("not a demo"), 0o600); err != nil {
		t.Fatal(err)
	}
	output := filepath.Join(t.TempDir(), "analysis.json")
	if code := run([]string{"analyze", "--input", input, "--output", output}); code != 20 {
		t.Fatalf("expected exit 20, got %d", code)
	}
	if _, err := os.Stat(output); !os.IsNotExist(err) {
		t.Fatalf("corrupt demo unexpectedly produced output: %v", err)
	}
}
