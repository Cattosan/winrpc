package main

/*
#include <stdlib.h>

typedef void (*TrackUpdateCallback)(const char* title, const char* artist, const char* album, const char* year, const char* quality, const char* coverUrl, const char* fileInfoJson);
typedef void (*StatusCallback)(const char* statusMsg);

static void invokeTrackUpdate(TrackUpdateCallback cb, const char* title, const char* artist, const char* album, const char* year, const char* quality, const char* coverUrl, const char* fileInfoJson) {
	if (cb != NULL) {
		cb(title, artist, album, year, quality, coverUrl, fileInfoJson);
	}
}

static void invokeStatus(StatusCallback cb, const char* msg) {
	if (cb != NULL) {
		cb(msg);
	}
}
*/
import "C"
import (
	"fmt"
	"sync"
	"time"
	"unsafe"
	"net/http"

	"winamprpc/discord"
	"winamprpc/winamp"
)

var (
	discordEnabled  bool
	discordWg       sync.WaitGroup
	bgRunning       bool
	bgWg            sync.WaitGroup
	trackCb         C.TrackUpdateCallback
	statusCb        C.StatusCallback
	lastTrackName   string
	lastWinampState bool = true // start true so it fires "No Winamp Detected" on first loop if missing
	lastPosition    int
	lastUpdate      time.Time
	lastPausedState bool
)

func init() {
	// Pre-warm the DNS and TLS pools for album cover fetching
	urls := []string{
		"https://itunes.apple.com",
		"https://ws.audioscrobbler.com",
	}
	for _, u := range urls {
		go func(url string) {
			http.Head(url)
		}(u)
	}
}

//export InitCallbacks
func InitCallbacks(tCb C.TrackUpdateCallback, sCb C.StatusCallback) {
	trackCb = tCb
	statusCb = sCb

	if !bgRunning {
		bgRunning = true
		bgWg.Add(1)
		go backgroundPoller()
	}
}

func setStatus(msg string) {
	if statusCb != nil {
		cMsg := C.CString(msg)
		C.invokeStatus(statusCb, cMsg)
		C.free(unsafe.Pointer(cMsg))
	}
}

func sendTrackUpdate(title, artist, album, year, quality, coverUrl, fileInfoJson string) {
	cTitle := C.CString(title)
	cArtist := C.CString(artist)
	cAlbum := C.CString(album)
	cYear := C.CString(year)
	cQuality := C.CString(quality)
	cCoverUrl := C.CString(coverUrl)
	cFileInfoJson := C.CString(fileInfoJson)

	C.invokeTrackUpdate(trackCb, cTitle, cArtist, cAlbum, cYear, cQuality, cCoverUrl, cFileInfoJson)

	C.free(unsafe.Pointer(cTitle))
	C.free(unsafe.Pointer(cArtist))
	C.free(unsafe.Pointer(cAlbum))
	C.free(unsafe.Pointer(cYear))
	C.free(unsafe.Pointer(cQuality))
	C.free(unsafe.Pointer(cCoverUrl))
	C.free(unsafe.Pointer(cFileInfoJson))
}

func backgroundPoller() {
	defer bgWg.Done()

	for bgRunning {
		info, err := winamp.GetTrackInfo()
		captureTime := time.Now()

		if err != nil {
			if lastWinampState {
				lastWinampState = false
				lastTrackName = ""
				sendTrackUpdate("No Winamp Detected", "", "", "", "", "music_icon", "")
				if discordEnabled {
					discord.ClearPresence()
				}
			}
		} else if !info.Playing {
			if lastTrackName != "" {
				lastTrackName = ""
				sendTrackUpdate("Stopped", "", "", "", "", "music_icon", "")
				if discordEnabled {
					discord.ClearPresence()
				}
			}
			lastWinampState = true
		} else {
			lastWinampState = true
			trackKey := info.Title + info.Artist

			elapsedMs := int(time.Since(lastUpdate).Milliseconds())
			expectedPosition := lastPosition + elapsedMs

			seekDetected := trackKey == lastTrackName && (info.Position < expectedPosition-3000 || info.Position > expectedPosition+3000)

			if trackKey != lastTrackName || seekDetected || info.IsPaused != lastPausedState {
				start := captureTime.Add(-time.Duration(info.Position) * time.Millisecond)
				end := start.Add(time.Duration(info.Length) * time.Millisecond)

				var coverUrl string
				if discordEnabled {
					if info.IsPaused {
						discord.ClearPresence()
						coverUrl = discord.FetchCoverArt(info.Title, info.Artist, info.Album)
					} else {
						coverUrl, _ = discord.UpdatePresence(info.Title, info.Artist, info.Album, info.Quality, start, end)
						setStatus("Presence Updated")
					}
				} else {
					coverUrl = discord.FetchCoverArt(info.Title, info.Artist, info.Album)
				}

				lastPosition = info.Position
				lastUpdate = captureTime
				
				shouldSendUpdate := trackKey != lastTrackName || seekDetected || info.IsPaused != lastPausedState
				lastPausedState = info.IsPaused

				if shouldSendUpdate {
					lastTrackName = trackKey

					uiCoverUrl := coverUrl
					if uiCoverUrl == "music_icon" && info.CoverPath != "" {
						uiCoverUrl = info.CoverPath
					}

					sendTrackUpdate(info.Title, info.Artist, info.Album, info.Year, info.Quality, uiCoverUrl, info.FileInfoJson)
				}
			}
		}

		time.Sleep(2 * time.Second)
	}
}

//export StartPresence
func StartPresence(clientID *C.char, lastFmKey *C.char) {
	if discordEnabled {
		return
	}
	
	cID := C.GoString(clientID)
	cLfKey := C.GoString(lastFmKey)
	discord.SetLastFmKey(cLfKey)

	err := discord.Init(cID)
	if err != nil {
		setStatus(fmt.Sprintf("Failed to init Discord: %v", err))
		return
	}
	
	discordEnabled = true
	setStatus("Connected to Discord")
	
	// Force an immediate update if Winamp is currently playing
	lastTrackName = ""
}

//export StopPresence
func StopPresence() {
	if discordEnabled {
		discordEnabled = false
		discord.Disconnect()
		setStatus("Disconnected from Discord")
	}
}

func main() {}
