#pragma once

#include "Engine.hpp"

namespace glassroom {
inline constexpr int ProtocolVersion = 1;

struct Reply {
    Json result;
    bool stateChanged{};
};

Reply dispatch(Engine&, const std::string& method, const Json& args);
Json event(const char* name, Json data);
Json success(int id, Json data);
Json failure(int id, const char* message);
}
