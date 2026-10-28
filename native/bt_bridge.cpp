#include <libtorrent/session.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/torrent_status.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/write_resume_data.hpp>
#include <libtorrent/download_priority.hpp>
#include <libtorrent/ip_filter.hpp>
#include <libtorrent/version.hpp>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <memory>
#include <thread>
#include <chrono>
#include <algorithm>
#include <stdexcept>
#include <iterator>
#include <windows.h>

namespace lt = libtorrent;
namespace fs = std::filesystem;
using namespace std::chrono_literals;
namespace {
thread_local std::string result;
thread_local std::string last_error;
constexpr int max_metadata = 4 * 1024 * 1024;

std::string quote(std::string const& text) {
    std::string out = "\"";
    for (unsigned char c : text) {
        if (c == '"' || c == '\\') { out += '\\'; out += char(c); }
        else if (c < 32) { char b[7]; sprintf_s(b, "\\u%04x", unsigned(c)); out += b; }
        else out += char(c);
    }
    return out + "\"";
}
std::string hash_key(lt::info_hash_t const& h) {
    std::ostringstream s;
    if (h.has_v1()) s << h.v1;
    else if (h.has_v2()) s << h.v2;
    else throw std::runtime_error("Torrent has no info hash");
    return s.str();
}
lt::add_torrent_params parse(std::string const& input) {
    lt::add_torrent_params p;
    if (input.rfind("magnet:?", 0) == 0) p = lt::parse_magnet_uri(input);
    else {
        lt::load_torrent_limits limits;
        limits.max_buffer_size = max_metadata;
        limits.max_pieces = 1024 * 1024;
        limits.max_decode_depth = 32;
        limits.max_decode_tokens = 300000;
        limits.max_directory_depth = 20;
        limits.max_duplicate_filenames = 0;
        p = lt::load_torrent_file(input, limits);
    }
    // Web seeds and explicit peer hints are not part of this release's network policy.
    p.url_seeds.clear(); p.peers.clear(); p.dht_nodes.clear();
    if (p.trackers.size() > 64) throw std::runtime_error("Too many trackers (maximum 64)");
    for (auto const& tracker : p.trackers)
        if (tracker.size() > 4096 || (tracker.rfind("http://", 0) != 0 && tracker.rfind("https://", 0) != 0 && tracker.rfind("udp://", 0) != 0))
            throw std::runtime_error("Unsupported tracker URL");
    return p;
}
std::string metadata_json(lt::torrent_info const* ti, std::string const& hash) {
    std::ostringstream s;
    s << "{\"Hash\":" << quote(hash) << ",\"Name\":" << quote(ti ? ti->name() : hash) << ",\"Files\":[";
    if (ti) {
        auto const& files = ti->layout();
        if (files.num_files() > 10000) throw std::runtime_error("Too many files (maximum 10000)");
        bool first = true;
        for (auto index : files.file_range()) {
            if (files.file_flags(index) & lt::file_storage::flag_symlink) throw std::runtime_error("Symlink torrents are not supported");
            if (!first) s << ',';
            first = false;
            s << "{\"Index\":" << static_cast<int>(index) << ",\"Path\":" << quote(files.file_path(index))
              << ",\"Size\":" << files.file_size(index) << ",\"Padding\":"
              << ((files.file_flags(index) & lt::file_storage::flag_pad_file) ? "true" : "false") << '}';
        }
    }
    s << "]}";
    return s.str();
}
struct job {
    std::unique_ptr<lt::session> session;
    lt::torrent_handle handle;
    std::string hash;
    fs::path resume;
    bool started = false;
    bool saving = false;
    bool paused = false;
    bool metadata_sent = false;
    bool restored = false;
    bool validating_resume = false;
    std::vector<lt::download_priority_t> selected_priorities;
    std::string fatal;
    std::string warning;
};
void save_file(fs::path const& path, std::vector<char> const& data) {
    auto temp = path; temp += L".tmp";
    std::ofstream f(temp, std::ios::binary | std::ios::trunc);
    f.exceptions(std::ios::failbit | std::ios::badbit);
    f.write(data.data(), static_cast<std::streamsize>(data.size()));
    f.flush(); f.close();
    if (!MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        throw std::runtime_error("Cannot atomically save BT resume data");
}
void alerts(job& j) {
    std::vector<lt::alert*> events;
    j.session->pop_alerts(&events);
    for (auto* event : events) {
        if (auto* saved = lt::alert_cast<lt::save_resume_data_alert>(event)) {
            // Never allow resume data to supply trackers, peers or save paths on next start.
            save_file(j.resume, lt::write_resume_data_buf(saved->params));
            j.saving = false;
        }
        else if (auto* failed = lt::alert_cast<lt::save_resume_data_failed_alert>(event)) {
            j.saving = false; j.fatal = "Cannot save resume data: " + failed->error.message();
        }
        else if (lt::alert_cast<lt::torrent_paused_alert>(event)) j.paused = true;
        else if (auto* torrent_error = lt::alert_cast<lt::torrent_error_alert>(event)) j.fatal = torrent_error->error.message();
        else if (auto* session_error = lt::alert_cast<lt::session_error_alert>(event)) j.fatal = session_error->error.message();
        else if (auto* listen_error = lt::alert_cast<lt::listen_failed_alert>(event)) j.fatal = "Selected network listen failed: " + listen_error->error.message();
        else if (lt::alert_cast<lt::tracker_error_alert>(event)) j.warning = "Tracker 暂时不可用，正在寻找其他节点";
    }
}
}
#define API extern "C" __declspec(dllexport)
API char const* nd_error() noexcept { return last_error.c_str(); }
API char const* nd_version() noexcept { return LIBTORRENT_VERSION; }
API char const* nd_inspect(char const* input) noexcept {
    try { auto p = parse(input); result = metadata_json(p.ti.get(), hash_key(p.info_hashes)); return result.c_str(); }
    catch (std::exception const& e) { last_error = e.what(); return nullptr; }
    catch (...) { last_error = "Unexpected native engine error"; return nullptr; }
}
API char const* nd_inspect_resume(char const* path) noexcept {
    try {
        auto resume = fs::u8path(path);
        if (fs::file_size(resume) > 64 * 1024 * 1024) throw std::runtime_error("Resume data exceeds 64 MiB");
        std::ifstream f(resume, std::ios::binary);
        std::vector<char> bytes((std::istreambuf_iterator<char>(f)), {});
        auto p = lt::read_resume_data(bytes);
        result = metadata_json(p.ti.get(), hash_key(p.info_hashes)); return result.c_str();
    } catch (std::exception const& e) { last_error = e.what(); return nullptr; }
    catch (...) { last_error = "Unexpected native engine error"; return nullptr; }
}
API void* nd_open(char const* input, char const* directory, char const* address, char const* resume_path, int options) noexcept {
    try {
        auto j = std::make_unique<job>();
        j->resume = fs::u8path(resume_path);
        auto p = parse(input);
        j->hash = hash_key(p.info_hashes);
        if (fs::exists(j->resume)) {
            if (fs::file_size(j->resume) > 64 * 1024 * 1024) throw std::runtime_error("Resume data exceeds 64 MiB");
            std::ifstream f(j->resume, std::ios::binary);
            std::vector<char> bytes((std::istreambuf_iterator<char>(f)), {});
            auto restored = lt::read_resume_data(bytes);
            if ((p.info_hashes.has_v1() && (!restored.info_hashes.has_v1() || p.info_hashes.v1 != restored.info_hashes.v1)) ||
                (p.info_hashes.has_v2() && (!restored.info_hashes.has_v2() || p.info_hashes.v2 != restored.info_hashes.v2)))
                throw std::runtime_error("Resume data belongs to another torrent");
            restored.renamed_files.clear();
            restored.part_file_dir.clear();
            restored.trackers = p.trackers; restored.tracker_tiers = p.tracker_tiers;
            restored.url_seeds.clear(); restored.peers.clear(); restored.dht_nodes.clear();
            if (p.ti) restored.ti = p.ti;
            // Resume v2 bitmaps are byte-aligned: up to seven trailing padding
            // bits are normal, not evidence of a missing/incompatible checkpoint.
            if (restored.ti && restored.have_pieces.size() >= restored.ti->num_pieces()
                && restored.have_pieces.size() <= restored.ti->num_pieces() + 7) {
                restored.have_pieces.resize(restored.ti->num_pieces());
                // Preserve unfinished 16 KiB blocks. force_recheck() discards their
                // picker state and would download them again on every restart.
                // Reintroduce completed pieces as full unfinished pieces instead:
                // libtorrent hashes them before marking them as present, while
                // keeping the genuinely unfinished block maps intact.
                for (auto piece : restored.ti->piece_range()) {
                    if (!restored.have_pieces[piece]) continue;
                    int const blocks = (restored.ti->piece_size(piece) + 16383) / 16384;
                    restored.unfinished_pieces[piece] = lt::bitfield(blocks, true);
                    j->validating_resume = true;
                }
                restored.have_pieces.clear_all();
                restored.verified_pieces.clear();
                j->restored = true;
            }
            p = std::move(restored);
        }
        // A known private torrent must not even bootstrap the session's DHT.
        bool const discovery = (options & 1) != 0;
        bool const dht = discovery && !(p.ti && p.ti->priv());
        lt::settings_pack settings;
        auto ip = lt::make_address(address);
        if (!ip.is_v4() || ip.is_unspecified()) throw std::runtime_error("A specific local IPv4 address is required");
        settings.set_str(lt::settings_pack::outgoing_interfaces, address);
        settings.set_str(lt::settings_pack::listen_interfaces, std::string(address) + ":0");
        settings.set_bool(lt::settings_pack::enable_dht, dht);
        settings.set_str(lt::settings_pack::dht_bootstrap_nodes, dht ?
            "dht.libtorrent.org:25401,router.bittorrent.com:6881,router.utorrent.com:6881" : "");
        settings.set_bool(lt::settings_pack::enable_lsd, false);
        settings.set_bool(lt::settings_pack::enable_upnp, false);
        settings.set_bool(lt::settings_pack::enable_natpmp, false);
        settings.set_bool(lt::settings_pack::enable_incoming_utp, true);
        settings.set_bool(lt::settings_pack::enable_outgoing_utp, true);
        settings.set_bool(lt::settings_pack::enable_incoming_tcp, (options & 2) == 0);
        settings.set_bool(lt::settings_pack::enable_outgoing_tcp, (options & 2) == 0);
        settings.set_bool(lt::settings_pack::validate_https_trackers, true);
        settings.set_bool(lt::settings_pack::ssrf_mitigation, true);
        settings.set_bool(lt::settings_pack::apply_ip_filter_to_trackers, true);
        settings.set_int(lt::settings_pack::connections_limit, 160);
        settings.set_int(lt::settings_pack::connection_speed, 12);
        settings.set_int(lt::settings_pack::upload_rate_limit, 512 * 1024);
        settings.set_int(lt::settings_pack::max_metadata_size, max_metadata);
        settings.set_int(lt::settings_pack::max_queued_disk_bytes, 16 * 1024 * 1024);
        settings.set_int(lt::settings_pack::max_piece_count, 1024 * 1024);
        settings.set_int(lt::settings_pack::alert_queue_size, 2048);
        settings.set_int(lt::settings_pack::alert_mask, lt::alert_category::error | lt::alert_category::storage | lt::alert_category::status | lt::alert_category::tracker);
        settings.set_str(lt::settings_pack::user_agent, "NetworkDownloader/2.0 libtorrent/" LIBTORRENT_VERSION);
        j->session = std::make_unique<lt::session>(settings);
        // No IPv6 socket may be used with this IPv4-only adapter selection.
        lt::ip_filter filter;
        filter.add_rule(lt::make_address("::"), lt::make_address("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), lt::ip_filter::blocked);
        j->session->set_ip_filter(filter);
        p.save_path = directory;
        p.storage_mode = lt::storage_mode_sparse;
        p.flags &= ~(lt::torrent_flags::auto_managed | lt::torrent_flags::paused | lt::torrent_flags::seed_mode);
        p.flags |= lt::torrent_flags::upload_mode | lt::torrent_flags::default_dont_download | lt::torrent_flags::apply_ip_filter;
        p.piece_priorities.clear();
        if (p.ti) p.file_priorities.assign(static_cast<std::size_t>(p.ti->num_files()), lt::dont_download);
        else p.file_priorities.clear();
        if (!discovery) { p.trackers.clear(); p.tracker_tiers.clear(); }
        j->handle = j->session->add_torrent(std::move(p));
        // Per-torrent limits also apply to loopback/LAN peers (which normally
        // bypass the session's global rate-limit peer class).
        j->handle.set_upload_limit(512 * 1024);
        return j.release();
    } catch (std::exception const& e) { last_error = e.what(); return nullptr; }
    catch (...) { last_error = "Unexpected native engine error"; return nullptr; }
}
API char const* nd_poll(void* ptr) noexcept {
    try {
        auto& j = *static_cast<job*>(ptr);
        alerts(j);
        auto status = j.handle.status();
        if (status.errc) j.fatal = status.errc.message();
        auto ti = status.has_metadata ? j.handle.torrent_file() : nullptr;
        bool const checking_files = status.state == lt::torrent_status::checking_files
            || status.state == lt::torrent_status::checking_resume_data;
        if (j.validating_resume && !checking_files) {
            std::vector<lt::partial_piece_info> queue;
            j.handle.get_download_queue(queue);
            j.validating_resume = std::any_of(queue.begin(), queue.end(), [](auto const& piece) {
                return piece.finished + piece.writing == piece.blocks_in_piece;
            });
        }
        bool const selection_ready = !j.started || status.is_seeding
            || j.handle.get_file_priorities() == j.selected_priorities;
        bool const checking = checking_files || j.validating_resume || !selection_ready;
        std::ostringstream s;
        s << "{\"Metadata\":" << (ti && !j.metadata_sent ? metadata_json(ti.get(), j.hash) : "null")
          << ",\"Downloaded\":" << status.total_wanted_done << ",\"Total\":" << status.total_wanted
          << ",\"Rate\":" << status.download_payload_rate << ",\"UploadRate\":" << status.upload_payload_rate
          << ",\"Peers\":" << status.num_peers << ",\"State\":" << static_cast<int>(status.state)
          << ",\"Checking\":" << (checking ? "true" : "false")
          << ",\"Finished\":" << (j.started && selection_ready && !checking && status.is_finished ? "true" : "false")
          << ",\"Port\":" << j.session->listen_port() << ",\"Error\":" << quote(j.fatal) << ",\"Warning\":" << quote(j.warning) << '}';
        if (ti) j.metadata_sent = true;
        result = s.str(); return result.c_str();
    } catch (std::exception const& e) { last_error = e.what(); return nullptr; }
    catch (...) { last_error = "Unexpected native engine error"; return nullptr; }
}
API int nd_start(void* ptr, int const* selected, int count) noexcept {
    try {
        auto& j = *static_cast<job*>(ptr);
        auto ti = j.handle.torrent_file();
        if (!ti || count <= 0 || count > ti->num_files()) throw std::runtime_error("Invalid file selection");
        std::vector<lt::download_priority_t> priorities(static_cast<std::size_t>(ti->num_files()), lt::dont_download);
        for (int i = 0; i < count; ++i) {
            if (selected[i] < 0 || selected[i] >= ti->num_files()) throw std::runtime_error("Invalid file index");
            priorities[static_cast<std::size_t>(selected[i])] = lt::default_priority;
        }
        j.handle.prioritize_files(priorities);
        j.selected_priorities = priorities;
        j.handle.unset_flags(lt::torrent_flags::upload_mode | lt::torrent_flags::default_dont_download);
        // Resume pieces are already being hash-verified without discarding
        // unfinished blocks. Only tasks without usable resume data need a scan.
        if (!j.restored) j.handle.force_recheck();
        j.handle.resume();
        j.started = true;
        return 1;
    } catch (std::exception const& e) { last_error = e.what(); return 0; }
    catch (...) { last_error = "Unexpected native engine error"; return 0; }
}
API int nd_save(void* ptr) noexcept {
    try {
        auto& j = *static_cast<job*>(ptr);
        if (!j.saving && j.handle.status().has_metadata) {
            j.saving = true;
            j.handle.save_resume_data(lt::torrent_handle::save_info_dict | lt::torrent_handle::flush_disk_cache);
        }
        return 1;
    } catch (std::exception const& e) { last_error = e.what(); return 0; }
    catch (...) { last_error = "Unexpected native engine error"; return 0; }
}
API int nd_stop(void* ptr) noexcept {
    try {
        auto& j = *static_cast<job*>(ptr);
        j.session->pause(); j.handle.pause();
        auto deadline = std::chrono::steady_clock::now() + 20s;
        while (!j.paused && std::chrono::steady_clock::now() < deadline) { alerts(j); std::this_thread::sleep_for(25ms); }
        if (!j.paused) throw std::runtime_error("Timed out flushing torrent data");
        if (!nd_save(ptr)) return 0;
        while (j.saving && std::chrono::steady_clock::now() < deadline) { alerts(j); std::this_thread::sleep_for(25ms); }
        if (j.saving) throw std::runtime_error("Timed out saving torrent resume data");
        if (!j.fatal.empty()) throw std::runtime_error(j.fatal);
        return 1;
    } catch (std::exception const& e) { last_error = e.what(); return 0; }
    catch (...) { last_error = "Unexpected native engine error"; return 0; }
}
API void nd_close(void* ptr) noexcept {
    try { delete static_cast<job*>(ptr); }
    catch (...) { }
}
// Deterministic local test support: no public trackers or DHT are needed.
API int nd_connect_peer(void* ptr, char const* address, int port, int tcp_only) noexcept {
    try {
        static_cast<job*>(ptr)->handle.connect_peer(lt::tcp::endpoint(lt::make_address(address), static_cast<unsigned short>(port)),
            {}, tcp_only ? lt::pex_encryption : lt::pex_encryption | lt::pex_utp);
        return 1;
    }
    catch (std::exception const& e) { last_error = e.what(); return 0; }
    catch (...) { last_error = "Unexpected native engine error"; return 0; }
}
