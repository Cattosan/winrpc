package winamp

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"unicode/utf8"
	"unsafe"

	"github.com/dhowden/tag"
	"golang.org/x/sys/windows"
)

var (
	user32               = windows.NewLazySystemDLL("user32.dll")
	kernel32             = windows.NewLazySystemDLL("kernel32.dll")
	procFindWindowW      = user32.NewProc("FindWindowW")
	procSendMessageW     = user32.NewProc("SendMessageW")
	procGetWindowThreadProcessId = user32.NewProc("GetWindowThreadProcessId")
	procOpenProcess      = kernel32.NewProc("OpenProcess")
	procReadProcessMemory = kernel32.NewProc("ReadProcessMemory")
	procCloseHandle      = kernel32.NewProc("CloseHandle")
)

const (
	WM_USER              = 0x0400
	IPC_ISPLAYING        = 104
	IPC_GETOUTPUTTIME    = 105
	IPC_GETLISTPOS       = 125
	IPC_GETPLAYLISTFILEW = 214
	PROCESS_VM_READ      = 0x0010
)

type TrackInfo struct {
	Title        string
	Artist       string
	Album        string
	Year         string
	Quality      string
	CoverPath    string
	Position     int
	Length       int
	Playing      bool
	IsPaused     bool
	FileInfoJson string
}

type FileInfoData struct {
	TrackNum      string `json:"trackNum"`
	DiscNum       string `json:"discNum"`
	Publisher     string `json:"publisher"`
	Channels      string `json:"channels"`
	BitDepth      string `json:"bitDepth"`
	SampleRate    string `json:"sampleRate"`
	Bitrate       string `json:"bitrate"`
	Format        string `json:"format"`
	FileSize      string `json:"fileSize"`
	Length        string `json:"length"`
	IsPaused      bool   `json:"isPaused"`
}

// containsGarbled checks if a string has sequences of '?' that suggest encoding failure
func containsGarbled(s string) bool {
	return strings.Contains(s, "???")
}

// readRawID3v2Frames reads raw ID3v2 text frames from a file, bypassing the tag library's
// encoding handling. This fixes cases where non-Latin text (Thai, etc.) is stored as UTF-8
// bytes but the ID3v2.3 encoding byte incorrectly says Latin-1, causing the tag library
// to replace them with '?'.
func readRawID3v2Frames(filePath string) (title, artist, album, year string) {
	debugLog := fmt.Sprintf("[RawParser] Attempting raw parse of: %s (hex: %x)\n", filePath, []byte(filePath))
	
	f, err := os.Open(filePath)
	if err != nil {
		debugLog += fmt.Sprintf("[RawParser] Failed to open file: %v\n", err)
		os.WriteFile(filepath.Join(os.TempDir(), "winrpc_rawparse_debug.log"), []byte(debugLog), 0644)
		return
	}
	defer f.Close()

	// Read the ID3v2 header (10 bytes)
	var header [10]byte
	if _, err := io.ReadFull(f, header[:]); err != nil {
		debugLog += fmt.Sprintf("[RawParser] Failed to read header: %v\n", err)
		os.WriteFile(filepath.Join(os.TempDir(), "winrpc_rawparse_debug.log"), []byte(debugLog), 0644)
		return
	}

	debugLog += fmt.Sprintf("[RawParser] First 10 bytes: %x\n", header[:])

	// Check for "ID3" magic
	if string(header[0:3]) != "ID3" {
		debugLog += "[RawParser] No ID3 magic found\n"
		os.WriteFile(filepath.Join(os.TempDir(), "winrpc_rawparse_debug.log"), []byte(debugLog), 0644)
		return
	}

	// ID3v2 version
	majorVer := header[3]
	debugLog += fmt.Sprintf("[RawParser] ID3v2.%d detected\n", majorVer)

	// Calculate tag size from syncsafe integer (last 4 bytes of header)
	tagSize := int(header[6])<<21 | int(header[7])<<14 | int(header[8])<<7 | int(header[9])
	debugLog += fmt.Sprintf("[RawParser] Tag size: %d bytes\n", tagSize)

	// Read the entire tag body
	tagData := make([]byte, tagSize)
	if _, err := io.ReadFull(f, tagData); err != nil {
		debugLog += fmt.Sprintf("[RawParser] Failed to read tag body: %v\n", err)
		os.WriteFile(filepath.Join(os.TempDir(), "winrpc_rawparse_debug.log"), []byte(debugLog), 0644)
		return
	}

	reader := bytes.NewReader(tagData)
	frameCount := 0

	for reader.Len() > 10 {
		var frameID [4]byte
		if _, err := io.ReadFull(reader, frameID[:]); err != nil {
			break
		}

		// Stop if we hit padding (0x00 bytes)
		if frameID[0] == 0 {
			break
		}

		var frameSize uint32
		if majorVer == 4 {
			var sizeBytes [4]byte
			if _, err := io.ReadFull(reader, sizeBytes[:]); err != nil {
				break
			}
			frameSize = uint32(sizeBytes[0])<<21 | uint32(sizeBytes[1])<<14 | uint32(sizeBytes[2])<<7 | uint32(sizeBytes[3])
		} else {
			if err := binary.Read(reader, binary.BigEndian, &frameSize); err != nil {
				break
			}
		}

		// Skip 2 flag bytes
		var flags [2]byte
		if _, err := io.ReadFull(reader, flags[:]); err != nil {
			break
		}

		if frameSize == 0 || int(frameSize) > reader.Len() {
			debugLog += fmt.Sprintf("[RawParser] Frame %s size %d exceeds remaining %d, stopping\n", string(frameID[:]), frameSize, reader.Len())
			break
		}

		frameData := make([]byte, frameSize)
		if _, err := io.ReadFull(reader, frameData); err != nil {
			break
		}

		id := string(frameID[:])
		frameCount++
		
		// Log all frames we encounter
		if id == "TIT2" || id == "TPE1" || id == "TALB" || id == "TDRC" || id == "TYER" {
			debugLog += fmt.Sprintf("[RawParser] Frame #%d: %s, size=%d, encoding=%d, rawHex=%x\n", frameCount, id, frameSize, frameData[0], frameData)
		} else {
			debugLog += fmt.Sprintf("[RawParser] Frame #%d: %s, size=%d (skipped)\n", frameCount, id, frameSize)
		}

		// Only process text frames we care about
		if id != "TIT2" && id != "TPE1" && id != "TALB" && id != "TDRC" && id != "TYER" {
			continue
		}

		if len(frameData) < 2 {
			continue
		}

		encoding := frameData[0]
		textBytes := frameData[1:]

		var text string
		switch encoding {
		case 0: // ISO-8859-1 — but often contains UTF-8 bytes
			cleaned := bytes.TrimRight(textBytes, "\x00")
			if utf8.Valid(cleaned) {
				text = string(cleaned)
			} else {
				runes := make([]rune, len(cleaned))
				for i, b := range cleaned {
					runes[i] = rune(b)
				}
				text = string(runes)
			}
		case 1: // UTF-16 with BOM
			cleaned := bytes.TrimRight(textBytes, "\x00")
			if len(cleaned) >= 2 {
				var order binary.ByteOrder
				if cleaned[0] == 0xFF && cleaned[1] == 0xFE {
					order = binary.LittleEndian
					cleaned = cleaned[2:]
				} else if cleaned[0] == 0xFE && cleaned[1] == 0xFF {
					order = binary.BigEndian
					cleaned = cleaned[2:]
				} else {
					order = binary.LittleEndian
				}
				for len(cleaned) >= 2 && cleaned[len(cleaned)-2] == 0 && cleaned[len(cleaned)-1] == 0 {
					cleaned = cleaned[:len(cleaned)-2]
				}
				runes := make([]rune, 0, len(cleaned)/2)
				for i := 0; i+1 < len(cleaned); i += 2 {
					var cp uint16
					if order == binary.LittleEndian {
						cp = uint16(cleaned[i]) | uint16(cleaned[i+1])<<8
					} else {
						cp = uint16(cleaned[i])<<8 | uint16(cleaned[i+1])
					}
					runes = append(runes, rune(cp))
				}
				text = string(runes)
			}
		case 2: // UTF-16BE without BOM
			cleaned := bytes.TrimRight(textBytes, "\x00")
			runes := make([]rune, 0, len(cleaned)/2)
			for i := 0; i+1 < len(cleaned); i += 2 {
				cp := uint16(cleaned[i])<<8 | uint16(cleaned[i+1])
				runes = append(runes, rune(cp))
			}
			text = string(runes)
		case 3: // UTF-8
			text = strings.TrimRight(string(textBytes), "\x00")
		}

		text = strings.TrimSpace(text)
		debugLog += fmt.Sprintf("[RawParser] Decoded %s: %s (hex: %x)\n", id, text, []byte(text))

		switch id {
		case "TIT2":
			title = text
		case "TPE1":
			artist = text
		case "TALB":
			album = text
		case "TDRC", "TYER":
			if year == "" {
				year = text
			}
		}
	}

	debugLog += fmt.Sprintf("[RawParser] Done. Parsed %d frames. Title=%s, Artist=%s, Album=%s\n", frameCount, title, artist, album)
	os.WriteFile(filepath.Join(os.TempDir(), "winrpc_rawparse_debug.log"), []byte(debugLog), 0644)

	return
}

func GetTrackInfo() (*TrackInfo, error) {
	className, _ := syscall.UTF16PtrFromString("Winamp v1.x")
	hwnd, _, _ := procFindWindowW.Call(uintptr(unsafe.Pointer(className)), 0)
	if hwnd == 0 {
		return nil, fmt.Errorf("winamp not running")
	}

	resPlaying, _, _ := procSendMessageW.Call(hwnd, WM_USER, 0, IPC_ISPLAYING)
	playing := resPlaying == 1 || resPlaying == 3 // 1=playing, 3=paused, 0=stopped
	if resPlaying == 0 {
		return &TrackInfo{Playing: false}, nil
	}

	resPosMs, _, _ := procSendMessageW.Call(hwnd, WM_USER, 0, IPC_GETOUTPUTTIME)
	resLengthSec, _, _ := procSendMessageW.Call(hwnd, WM_USER, 1, IPC_GETOUTPUTTIME)

	listPos, _, _ := procSendMessageW.Call(hwnd, WM_USER, 0, IPC_GETLISTPOS)
	ptr, _, _ := procSendMessageW.Call(hwnd, WM_USER, listPos, IPC_GETPLAYLISTFILEW)

	var pid uint32
	procGetWindowThreadProcessId.Call(hwnd, uintptr(unsafe.Pointer(&pid)))

	hProcess, _, _ := procOpenProcess.Call(PROCESS_VM_READ, 0, uintptr(pid))
	if hProcess == 0 {
		return nil, fmt.Errorf("could not open winamp process")
	}
	defer procCloseHandle.Call(hProcess)

	var buf [1024]uint16
	var bytesRead uintptr
	procReadProcessMemory.Call(
		hProcess,
		ptr,
		uintptr(unsafe.Pointer(&buf[0])),
		uintptr(len(buf)*2),
		uintptr(unsafe.Pointer(&bytesRead)),
	)

	filePath := syscall.UTF16ToString(buf[:])

	info := &TrackInfo{
		Playing:  playing,
		IsPaused: resPlaying == 3,
		Position: int(resPosMs),
		Length:   int(resLengthSec) * 1000,
	}
	
	fileData := FileInfoData{
		IsPaused: resPlaying == 3,
	}

	f, err := os.Open(filePath)
	if err == nil {
		defer f.Close()
		
		if stat, err := f.Stat(); err == nil {
			fileData.FileSize = fmt.Sprintf("%d bytes", stat.Size())
		}
		
		if info.Length > 0 {
			fileData.Length = fmt.Sprintf("%d seconds", info.Length/1000)
		}
		
		// Check if it's a FLAC file. If so, we want to skip any corrupted ID3v2 tags
		// (which are non-standard but often slapped on by buggy rippers) and force it
		// to read the true FLAC Vorbis Comments.
		var m tag.Metadata
		var err error
		
		isFlac := strings.ToLower(filepath.Ext(filePath)) == ".flac"
		if isFlac {
			var header [10]byte
			f.Read(header[:])
			if string(header[0:3]) == "ID3" {
				// Calculate tag size and skip past the ID3 block
				tagSize := int(header[6])<<21 | int(header[7])<<14 | int(header[8])<<7 | int(header[9])
				f.Seek(int64(10+tagSize), io.SeekStart)
			} else {
				f.Seek(0, io.SeekStart)
			}
			m, err = tag.ReadFLACTags(f)
		} else {
			m, err = tag.ReadFrom(f)
		}

		if err == nil {
			info.Title = m.Title()
			info.Artist = m.Artist()
			info.Album = m.Album()
			
			// Fallback: if the tag library returned garbled '???' text, try our raw ID3v2 parser
			if containsGarbled(info.Title) || containsGarbled(info.Artist) || containsGarbled(info.Album) {
				rawTitle, rawArtist, rawAlbum, rawYear := readRawID3v2Frames(filePath)
				if rawTitle != "" && !containsGarbled(rawTitle) {
					info.Title = rawTitle
				}
				if rawArtist != "" && !containsGarbled(rawArtist) {
					info.Artist = rawArtist
				}
				if rawAlbum != "" && !containsGarbled(rawAlbum) {
					info.Album = rawAlbum
				}
				if rawYear != "" {
					info.Year = rawYear
				}
			}
			
			// Debug: log raw tag data to trace encoding issues
			debugLog := fmt.Sprintf("[TagDebug] File: %s\n[TagDebug] Title: %s (hex: %x)\n[TagDebug] Artist: %s (hex: %x)\n[TagDebug] Album: %s (hex: %x)\n[TagDebug] Format: %s, FileType: %s\n",
				filePath, info.Title, []byte(info.Title), info.Artist, []byte(info.Artist), info.Album, []byte(info.Album), m.Format(), m.FileType())
			logPath := filepath.Join(os.TempDir(), "winrpc_tag_debug.log")
			os.WriteFile(logPath, []byte(debugLog), 0644)
			if m.Year() != 0 {
				info.Year = fmt.Sprintf("%d", m.Year())
			}

			if trk, maxTrk := m.Track(); trk > 0 {
				if maxTrk > 0 {
					fileData.TrackNum = fmt.Sprintf("%d/%d", trk, maxTrk)
				} else {
					fileData.TrackNum = fmt.Sprintf("%d", trk)
				}
			}
			if dsc, maxDsc := m.Disc(); dsc > 0 {
				if maxDsc > 0 {
					fileData.DiscNum = fmt.Sprintf("%d/%d", dsc, maxDsc)
				} else {
					fileData.DiscNum = fmt.Sprintf("%d", dsc)
				}
			}
			
			// Try to get publisher from raw ID3/MP4/Vorbis tags
			raw := m.Raw()
			for k, v := range raw {
				upperK := strings.ToUpper(k)
				if upperK == "TPUB" || upperK == "©PUB" || upperK == "PUBLISHER" || upperK == "ORGANIZATION" || upperK == "LABEL" {
					if arr, ok := v.([]string); ok && len(arr) > 0 {
						fileData.Publisher = strings.Join(arr, ", ")
					} else {
						fileData.Publisher = fmt.Sprintf("%v", v)
					}
					break
				}
			}

			// Extract embedded album art
			if p := m.Picture(); p != nil && len(p.Data) > 0 {
				tmpDir := os.TempDir()
				coverFile := filepath.Join(tmpDir, "winrpc_cover.jpg")
				if err := os.WriteFile(coverFile, p.Data, 0644); err == nil {
					info.CoverPath = coverFile
				}
			}
		}
		
		if info.Title == "" {
			base := filepath.Base(filePath)
			info.Title = strings.TrimSuffix(base, filepath.Ext(base))
		}
		if info.Artist == "" {
			info.Artist = "Unknown Artist"
		}
		if info.Album == "" {
			info.Album = "Unknown Album"
		}
		if info.Year == "" {
			info.Year = "Unknown Year"
		}
		
		ext := strings.ToLower(filepath.Ext(filePath))
		if len(ext) > 0 {
			fileData.Format = strings.ToUpper(ext[1:])
		}
		
		resSampleRate, _, _ := procSendMessageW.Call(hwnd, WM_USER, 0, 126)
		resBitrate, _, _ := procSendMessageW.Call(hwnd, WM_USER, 1, 126)
		resChannels, _, _ := procSendMessageW.Call(hwnd, WM_USER, 2, 126)
		
		if resChannels == 1 {
			fileData.Channels = "Mono"
		} else if resChannels == 2 {
			fileData.Channels = "Stereo"
		} else if resChannels > 2 {
			fileData.Channels = fmt.Sprintf("%d Channels", resChannels)
		} else {
			fileData.Channels = "Unknown"
		}
		
		if resSampleRate > 0 {
			fileData.SampleRate = fmt.Sprintf("%d Hz", resSampleRate)
		}
		
		if ext == ".flac" {
			// Parse FLAC header manually for exact Bit Depth and Sample Rate
			var header [42]byte
			f.Seek(0, 0) // reset cursor
			if n, _ := f.Read(header[:]); n >= 22 && string(header[0:4]) == "fLaC" {
				sampleRate := (uint32(header[18]) << 12) | (uint32(header[19]) << 4) | (uint32(header[20]) >> 4)
				bps := ((header[20] & 0x01) << 4) | (header[21] >> 4) + 1
				
				fileData.BitDepth = fmt.Sprintf("%d bit", bps)
				fileData.SampleRate = fmt.Sprintf("%d Hz", sampleRate)
				info.Quality = fmt.Sprintf("%dbit / %gkHz", bps, float64(sampleRate)/1000.0)
				
				// FLAC compression ratio approximation if length > 0
				if info.Length > 0 && resBitrate > 0 {
					uncompressedBitrate := float64(sampleRate * uint32(bps) * uint32(resChannels))
					if uncompressedBitrate > 0 {
						ratio := (float64(resBitrate*1000) / uncompressedBitrate) * 100.0
						fileData.Bitrate = fmt.Sprintf("%d kbps (%.1f%% compressed)", resBitrate, ratio)
					} else {
						fileData.Bitrate = fmt.Sprintf("%d kbps", resBitrate)
					}
				} else {
					fileData.Bitrate = fmt.Sprintf("%d kbps", resBitrate)
				}
			} else {
				info.Quality = "FLAC Lossless"
				fileData.Bitrate = fmt.Sprintf("%d kbps", resBitrate)
			}
		} else {
			if resBitrate > 0 {
				fileData.Bitrate = fmt.Sprintf("%d kbps", resBitrate)
			}
			fileData.BitDepth = "N/A" // usually lossy formats don't expose bit depth cleanly
			
			if resSampleRate > 0 && resBitrate > 0 {
				info.Quality = fmt.Sprintf("%dkbps / %gkHz", resBitrate, float64(resSampleRate)/1000.0)
			} else if resBitrate > 0 {
				info.Quality = fmt.Sprintf("%dkbps", resBitrate)
			} else {
				info.Quality = "Standard Audio"
			}
		}
	} else {
		info.Title = "Unknown Track"
		info.Artist = "Unknown Artist"
		info.Album = "Unknown Album"
		info.Year = "Unknown Year"
		info.Quality = "Unknown Quality"
	}
	
	jsonBytes, _ := json.Marshal(fileData)
	info.FileInfoJson = string(jsonBytes)

	return info, nil
}
