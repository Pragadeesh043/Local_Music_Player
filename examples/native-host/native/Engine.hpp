#pragma once

#include <nlohmann/json.hpp>

#include <cstddef>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace glassroom {
using Json = nlohmann::json;

struct Track {
    std::string id, title, artist, albumId, album, artwork;
    double duration{};
    int number{};
};

struct Playback {
    std::optional<Track> track;
    bool playing{};
    double position{};
    double volume{1};
    std::vector<Track> queue;
    int queueIndex{-1};
};

struct LibraryQuery {
    std::string kind, query, albumId, playlistId;
    std::size_t offset{}, limit{60};
};

struct PlayRequest {
    std::string id, albumId, playlistId;
};

struct Lyric {
    double at{};
    std::string text;
};

struct Progress {
    std::string id;
    double position{};
};

struct Update {
    bool stateChanged{};
    std::optional<Progress> progress;
};

// Called on one bridge worker. Dispatch engine-affine work from your adapter.
// Report externally initiated track, queue, transport or volume changes via poll().
class Engine {
public:
    virtual ~Engine() = default;
    virtual Playback state() = 0;
    // Return { "items": [...], "total": number }; row schemas are in bridge.ts.
    virtual Json library(const LibraryQuery&) = 0;
    virtual std::vector<Lyric> lyrics(const std::string& id) = 0;
    virtual void play(const PlayRequest&) = 0;
    virtual void pause() = 0;
    virtual void seek(const std::string& id, double seconds) = 0;
    virtual void volume(double value) = 0;
    virtual void next() = 0;
    virtual void previous() = 0;
    virtual void enqueue(const std::string& id) = 0;
    virtual void remove(std::size_t index, const std::string& id) = 0;
    virtual void jump(std::size_t index, const std::string& id) = 0;
    virtual Update poll() = 0;
};

std::unique_ptr<Engine> makeDemoEngine(const Json& catalog);
void to_json(Json&, const Track&);
void to_json(Json&, const Playback&);
void to_json(Json&, const Lyric&);
void to_json(Json&, const Progress&);
}
