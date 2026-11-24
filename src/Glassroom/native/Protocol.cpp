#include "Protocol.hpp"

#include <cmath>
#include <stdexcept>
#include <utility>

namespace glassroom {
namespace {
std::string field(const Json& args, const char* key, bool required = false)
{
    auto value = args.value(key, std::string{});
    if ((required && value.empty()) || value.size() > 1024)
        throw std::invalid_argument("Invalid command text.");
    return value;
}

std::size_t integer(const Json& args, const char* key, int min, int max)
{
    const auto& value = args.at(key);
    if (!value.is_number_integer() || value < min || value > max)
        throw std::invalid_argument("Invalid command index.");
    return value.get<std::size_t>();
}

double number(const Json& args, const char* key, double min, double max)
{
    const auto value = args.at(key).get<double>();
    if (!std::isfinite(value) || value < min || value > max)
        throw std::invalid_argument("Invalid command value.");
    return value;
}
}

Reply dispatch(Engine& engine, const std::string& method, const Json& args)
{
    if (!args.is_object()) throw std::invalid_argument("Invalid command arguments.");
    if (method == "state") return {engine.state()};
    if (method == "library") {
        const auto kind = field(args, "kind", true);
        if (kind != "tracks" && kind != "albums" && kind != "playlists")
            throw std::invalid_argument("Unknown library view.");
        return {engine.library({kind, field(args, "query"), field(args, "albumId"), field(args, "playlistId"),
                                integer(args, "offset", 0, 100'000'000), integer(args, "limit", 1, 200)})};
    }
    if (method == "lyrics") return {engine.lyrics(field(args, "id", true))};

    if (method == "play") engine.play({field(args, "id"), field(args, "albumId"), field(args, "playlistId")});
    else if (method == "pause") engine.pause();
    else if (method == "seek") engine.seek(field(args, "id", true), number(args, "position", 0, 31'536'000));
    else if (method == "volume") engine.volume(number(args, "value", 0, 1));
    else if (method == "next") engine.next();
    else if (method == "previous") engine.previous();
    else if (method == "enqueue") engine.enqueue(field(args, "id", true));
    else if (method == "remove") engine.remove(integer(args, "index", 0, 1'000'000), field(args, "id", true));
    else if (method == "jump") engine.jump(integer(args, "index", 0, 1'000'000), field(args, "id", true));
    else throw std::invalid_argument("Unknown player command.");
    return {nullptr, true};
}

void to_json(Json& json, const Track& track)
{
    json = {{"id", track.id}, {"title", track.title}, {"artist", track.artist}, {"albumId", track.albumId},
            {"album", track.album}, {"artwork", track.artwork}, {"duration", track.duration}, {"number", track.number}};
}
void to_json(Json& json, const Playback& state)
{
    json = {{"track", state.track ? Json(*state.track) : Json(nullptr)}, {"playing", state.playing},
            {"position", state.position}, {"volume", state.volume}, {"queue", state.queue}, {"queueIndex", state.queueIndex}};
}
void to_json(Json& json, const Lyric& line) { json = {{"at", line.at}, {"text", line.text}}; }
void to_json(Json& json, const Progress& progress) { json = {{"id", progress.id}, {"position", progress.position}}; }
Json event(const char* name, Json data) { return {{"v", ProtocolVersion}, {"event", name}, {"data", std::move(data)}}; }
Json success(int id, Json data) { return {{"v", ProtocolVersion}, {"id", id}, {"ok", true}, {"result", std::move(data)}}; }
Json failure(int id, const char* message) { return {{"v", ProtocolVersion}, {"id", id}, {"ok", false}, {"error", message}}; }
}
